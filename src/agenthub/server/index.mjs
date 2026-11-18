// Serviço de eventos da Central dos Agentes.
//
// Recebe eventos do encaminhador de hooks, guarda o histórico em arquivo,
// deriva o estado dos agentes e o envia em tempo real para a interface.
// Escuta só em 127.0.0.1 e não conhece o servidor de produção nem as
// máquinas das atendentes.

import http from 'node:http';
import { createReadStream, existsSync, mkdirSync, readFileSync, writeFileSync, appendFileSync, statSync } from 'node:fs';
import { dirname, resolve, extname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { createStore, normalizeEvent } from './store.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const DATA_DIR = resolve(HERE, '..', 'data');
const DIST_DIR = resolve(HERE, '..', 'dist');
const KEY_FILE = resolve(DATA_DIR, 'ingest.key');
const EVENTS_FILE = resolve(DATA_DIR, 'events.jsonl');
const HOST = '127.0.0.1';
const PORT = Number(process.env.PORT || 4317);
const MAX_BODY = 1024 * 1024;
const REPLAY_LINES = 5000;

const CONTENT_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
};

mkdirSync(DATA_DIR, { recursive: true });
if (!existsSync(KEY_FILE)) writeFileSync(KEY_FILE, randomBytes(24).toString('hex') + '\n', { mode: 0o600 });
const INGEST_KEY = Buffer.from(readFileSync(KEY_FILE, 'utf8').trim());

const store = createStore();

// Reconstrói o estado a partir do histórico gravado.
if (existsSync(EVENTS_FILE)) {
  const lines = readFileSync(EVENTS_FILE, 'utf8').split('\n').filter(Boolean).slice(-REPLAY_LINES);
  for (const line of lines) {
    try {
      const event = normalizeEvent(JSON.parse(line));
      if (event) store.apply(event);
    } catch {
      // linha corrompida: ignorada
    }
  }
}

const clients = new Set();

function sendEvent(res, name, payload) {
  res.write(`event: ${name}\ndata: ${JSON.stringify(payload)}\n\n`);
}

function snapshot() {
  return { agents: store.list(), diagnostics: store.diagnostics(), serverTime: new Date().toISOString() };
}

function broadcast(name, payload) {
  for (const res of clients) sendEvent(res, name, payload);
}

// O estado "sem eventos há muito tempo" depende do relógio, não de um evento.
// Por isso o retrato completo é reenviado periodicamente.
setInterval(() => {
  if (clients.size > 0) broadcast('snapshot', snapshot());
}, 20_000).unref();

function json(res, status, body) {
  const data = JSON.stringify(body);
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
  res.end(data);
}

function keyMatches(header) {
  if (typeof header !== 'string') return false;
  const given = Buffer.from(header.trim());
  return given.length === INGEST_KEY.length && timingSafeEqual(given, INGEST_KEY);
}

function readBody(req) {
  return new Promise((resolveBody, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY) {
        reject(new Error('corpo grande demais'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => resolveBody(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

async function ingest(req, res) {
  if (!keyMatches(req.headers['x-agenthub-key'])) return json(res, 401, { error: 'chave inválida' });
  let body;
  try {
    body = JSON.parse(await readBody(req));
  } catch {
    return json(res, 400, { error: 'JSON inválido' });
  }
  const incoming = Array.isArray(body?.events) ? body.events : [];
  const changed = new Set();
  let accepted = 0;
  const lines = [];
  for (const raw of incoming.slice(0, 2000)) {
    const event = normalizeEvent(raw);
    if (!event) continue;
    accepted += 1;
    lines.push(JSON.stringify(event));
    for (const key of store.apply(event)) changed.add(key);
  }
  if (lines.length > 0) appendFileSync(EVENTS_FILE, lines.join('\n') + '\n');
  if (changed.size > 0) {
    broadcast('update', {
      agents: [...changed].map((key) => store.get(key)).filter(Boolean),
      diagnostics: store.diagnostics(),
      serverTime: new Date().toISOString(),
    });
  }
  return json(res, 202, { accepted, rejected: incoming.length - accepted });
}

function stream(req, res) {
  res.writeHead(200, {
    'content-type': 'text/event-stream; charset=utf-8',
    'cache-control': 'no-store',
    connection: 'keep-alive',
  });
  res.write('retry: 2000\n\n');
  sendEvent(res, 'snapshot', snapshot());
  clients.add(res);
  req.on('close', () => clients.delete(res));
}

function serveStatic(pathname, res) {
  const wanted = pathname === '/' ? '/index.html' : decodeURIComponent(pathname);
  let file = resolve(DIST_DIR, '.' + wanted);
  if (file !== DIST_DIR && !file.startsWith(DIST_DIR + sep)) {
    res.writeHead(403).end();
    return;
  }
  if (!existsSync(file) || !statSync(file).isFile()) file = resolve(DIST_DIR, 'index.html');
  if (!existsSync(file)) {
    res.writeHead(503, { 'content-type': 'text/plain; charset=utf-8' });
    res.end('A interface ainda não foi compilada. Rode "npm run build" em src/agenthub.');
    return;
  }
  res.writeHead(200, {
    'content-type': CONTENT_TYPES[extname(file)] || 'application/octet-stream',
    'cache-control': file.endsWith('index.html') ? 'no-store' : 'public, max-age=3600',
  });
  createReadStream(file).pipe(res);
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url || '/', `http://${HOST}:${PORT}`);
  const route = `${req.method} ${url.pathname}`;
  if (route === 'POST /api/v1/agent-events') {
    ingest(req, res).catch(() => json(res, 500, { error: 'falha ao receber eventos' }));
  } else if (route === 'GET /api/v1/stream') {
    stream(req, res);
  } else if (route === 'GET /api/v1/agents') {
    json(res, 200, snapshot());
  } else if (route === 'GET /api/v1/health') {
    json(res, 200, { ok: true, agents: store.list().length, events: store.diagnostics().total });
  } else if (url.pathname.startsWith('/api/')) {
    json(res, 404, { error: 'rota inexistente' });
  } else if (req.method === 'GET') {
    serveStatic(url.pathname, res);
  } else {
    res.writeHead(405).end();
  }
});

server.listen(PORT, HOST, () => {
  console.log(`Central dos Agentes em http://${HOST}:${PORT}`);
  console.log(`Histórico: ${EVENTS_FILE}`);
});
