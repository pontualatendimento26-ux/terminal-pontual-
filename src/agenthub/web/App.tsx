import { useEffect, useMemo, useRef, useState } from 'react';
import Phaser from 'phaser';
import { OfficeScene } from './OfficeScene';
import {
  ACTIVITY_LABEL,
  STATE_GLYPH,
  STATE_LABEL,
  connectHub,
  errorLong,
  type AgentEvent,
  type AgentState,
  type AgentView,
  type Diagnostics,
} from './hub';

const MAX_DESKS = 16;
const HIDE_DISCONNECTED_AFTER_MS = 2 * 60 * 60 * 1000;
const STATE_ORDER: AgentState[] = ['working', 'waiting', 'done', 'error', 'unknown', 'disconnected'];

function clock(iso: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function elapsed(fromIso: string, now: number): string {
  const seconds = Math.max(0, Math.floor((now - Date.parse(fromIso)) / 1000));
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

function describeEvent(event: AgentEvent): string {
  const tool = event.tool ?? 'ferramenta';
  switch (event.type) {
    case 'session.started':
      return 'Sessão iniciada';
    case 'session.ended':
      return 'Sessão encerrada';
    case 'prompt.submitted':
      return 'Instrução recebida';
    case 'tool.started':
      if (event.activity === 'delegating') {
        return event.taskSubject ? `Lançou subagente: ${event.taskSubject}` : 'Lançou subagente';
      }
      return event.file ? `Iniciou ${tool} em ${event.file}` : `Iniciou ${tool}`;
    case 'tool.finished':
      return event.file ? `Concluiu ${tool} em ${event.file}` : `Concluiu ${tool}`;
    case 'tool.failed':
      if (event.errorCode === 'interrupted') return `Você interrompeu ${tool}`;
      return event.errorMessage ? `Falha em ${tool}: ${event.errorMessage}` : `Falha em ${tool}`;
    case 'permission.requested':
      return `Pediu permissão para ${tool}`;
    case 'notification':
      return event.detail ? `Notificação (${event.detail})` : 'Notificação';
    case 'agent.started':
      return 'Subagente iniciado';
    case 'agent.stopped':
      return 'Subagente concluído';
    case 'task.created':
      return `Tarefa criada: ${event.taskSubject ?? 'sem título'}`;
    case 'task.completed':
      return `Tarefa concluída: ${event.taskSubject ?? 'sem título'}`;
    case 'turn.stopped':
      return 'Turno concluído';
    case 'turn.failed':
      return event.errorMessage ? `Turno encerrado com erro: ${event.errorMessage}` : 'Turno encerrado com erro';
    default:
      return `Evento ${event.hookEvent ?? event.type}`;
  }
}

function activityLine(agent: AgentView): string {
  if (agent.state === 'error') return errorLong(agent.lastError);
  if (agent.state !== 'working') return agent.stateReason ?? STATE_LABEL[agent.state];
  const label = agent.activity ? ACTIVITY_LABEL[agent.activity] ?? 'Trabalhando' : 'Trabalhando';
  if (agent.currentFile) return `${label}: ${agent.currentFile}`;
  if (agent.currentTool) return `${label} (${agent.currentTool})`;
  return label;
}

const ACTIVITY_VERB: Record<string, string> = {
  coding: 'escrevendo ou editando código',
  analyzing: 'lendo e pesquisando',
  testing: 'rodando testes',
  delegating: 'lançando um subagente',
  working: 'executando uma ferramenta',
};

function span(fromIso: string, toMs: number): string {
  const seconds = Math.max(0, Math.round((toMs - Date.parse(fromIso)) / 1000));
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? `${minutes} min ${seconds % 60} s` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** Explica em frases o que o agente faz ou fez, usando só os eventos recebidos. */
function summarize(agent: AgentView, parent: AgentView | null, now: number): string[] {
  const lines: string[] = [];

  let intro =
    agent.kind === 'main'
      ? 'É a sessão principal do Claude Code'
      : `É um subagente${agent.agentType ? ` com a função ${agent.agentType}` : ''}${parent ? `, lançado por ${parent.nickname}` : ''}`;
  if (agent.project) intro += `, no projeto ${agent.project}`;
  intro += agent.startedAt ? `. Começou às ${clock(agent.startedAt)}.` : '.';
  lines.push(intro);

  if (agent.task) lines.push(`Tarefa recebida: ${agent.task}.`);

  switch (agent.state) {
    case 'working': {
      const verb = agent.activity ? ACTIVITY_VERB[agent.activity] ?? 'trabalhando' : 'trabalhando';
      let text = `Agora está ${verb}`;
      if (agent.currentTool) text += ` (${agent.currentTool})`;
      if (agent.currentFile) text += ` em ${agent.currentFile}`;
      lines.push(`${text}.`);
      break;
    }
    case 'waiting':
      lines.push('Está parado, aguardando uma instrução ou uma permissão.');
      break;
    case 'done':
      if (agent.kind === 'subagent' && agent.endedAt) {
        const took = agent.startedAt ? `, depois de ${span(agent.startedAt, Date.parse(agent.endedAt))} de trabalho` : '';
        lines.push(`Terminou às ${clock(agent.endedAt)}${took}, e devolveu o resultado a quem o lançou.`);
      } else {
        lines.push(`Concluiu o último turno às ${clock(agent.lastEventAt)} e aguarda a próxima instrução.`);
      }
      break;
    case 'error': {
      const error = agent.lastError;
      lines.push(`Parou com erro às ${clock(error?.at ?? agent.lastEventAt)}: ${errorLong(error)}.`);
      if (error?.message) lines.push(`Mensagem do erro: "${error.message}"`);
      else lines.push('O motivo não foi registrado (a falha aconteceu antes de a Central guardar os motivos).');
      break;
    }
    case 'disconnected':
      lines.push(`A sessão foi encerrada às ${clock(agent.endedAt ?? agent.lastEventAt)}.`);
      break;
    case 'unknown':
      lines.push(agent.stateReason ? `${agent.stateReason}.` : 'Não há eventos suficientes para dizer o que está fazendo.');
      break;
  }

  const tools = Object.entries(agent.tools).sort((a, b) => b[1] - a[1]);
  const uses = tools.reduce((sum, [, count]) => sum + count, 0);
  if (uses > 0) {
    const top = tools
      .slice(0, 3)
      .map(([tool, count]) => `${tool} (${count}×)`)
      .join(', ');
    const verb = agent.state === 'working' ? 'Até agora usou' : 'Usou';
    lines.push(`${verb} ferramentas ${uses} ${uses === 1 ? 'vez' : 'vezes'}, principalmente ${top}.`);
  }
  if (agent.files.length > 0) {
    lines.push(
      agent.files.length === 1
        ? `Mexeu em 1 arquivo: ${agent.files[0]}.`
        : `Mexeu em ${agent.files.length} arquivos; o mais recente foi ${agent.files[0]}.`,
    );
  }
  if (agent.tasksTotal > 0) lines.push(`Registrou ${agent.tasksTotal} tarefas e concluiu ${agent.tasksDone}.`);
  if (agent.errorCount > 0) {
    lines.push(`${agent.errorCount} ${agent.errorCount === 1 ? 'ação falhou' : 'ações falharam'} no caminho.`);
  }
  if (agent.state === 'working') lines.push(`Último sinal há ${elapsed(agent.lastEventAt, now)}.`);
  return lines;
}

function StateChip({ state }: { state: AgentState }) {
  return (
    <span className={`chip chip-${state}`}>
      <span className="chip-glyph" aria-hidden="true">
        {STATE_GLYPH[state]}
      </span>
      {STATE_LABEL[state]}
    </span>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="field">
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}

function AgentPanel({ agent, parent, now, onClose }: { agent: AgentView; parent: AgentView | null; now: number; onClose: () => void }) {
  const tools = Object.entries(agent.tools).sort((a, b) => b[1] - a[1]);
  const events = [...agent.events].reverse().slice(0, 40);
  return (
    <aside className="panel" aria-label={`Detalhes de ${agent.nickname}`}>
      <header className="panel-head">
        <div>
          <h2>{agent.nickname}</h2>
          <p className="panel-kind">
            {agent.kind === 'main' ? 'Agente principal' : 'Subagente'}
            {parent ? ` de ${parent.nickname}` : ''}
          </p>
        </div>
        <button type="button" className="ghost" onClick={onClose} aria-label="Fechar detalhes">
          Fechar
        </button>
      </header>

      <section className="story">
        <h3>{agent.state === 'working' || agent.state === 'waiting' ? 'O que está fazendo' : 'O que fez'}</h3>
        {summarize(agent, parent, now).map((line) => (
          <p key={line}>{line}</p>
        ))}
        <p className="note">
          Resumo montado a partir dos eventos recebidos. O conteúdo do trabalho e o resultado não são enviados à Central.
        </p>
      </section>

      <dl className="fields">
        <Field label="Estado">
          <StateChip state={agent.state} />
          {agent.stateReason && <span className="note">{agent.stateReason}</span>}
        </Field>
        <Field label="Nome">
          {agent.nickname}
          <span className="note">Apelido dado pela Central para distinguir os personagens</span>
        </Field>
        <Field label="Função">
          {agent.agentType ?? (
            <span className="absent">
              {agent.kind === 'main' ? 'Os hooks não dão nome à sessão principal' : 'O hook não informou o tipo'}
            </span>
          )}
        </Field>
        <Field label="Tarefa atual">
          {agent.task ? (
            <>
              {agent.task}
              <span className="note">
                {agent.taskSource === 'spawn' ? 'Descrição dada ao lançar o subagente' : 'Tarefa registrada pelo agente'}
              </span>
            </>
          ) : (
            <span className="absent">Não informada pelos hooks</span>
          )}
        </Field>
        {agent.tasksTotal > 0 && (
          <Field label="Tarefas">
            {agent.tasksDone} de {agent.tasksTotal} concluídas
          </Field>
        )}
        {agent.state === 'error' ? (
          <Field label="Motivo do erro">
            {errorLong(agent.lastError)}
            {agent.lastError?.message && <span className="error-message">{agent.lastError.message}</span>}
          </Field>
        ) : (
          <Field label="Atividade agora">{activityLine(agent)}</Field>
        )}
        <Field label="Início">
          {agent.startedAt ? (
            `${clock(agent.startedAt)} (há ${elapsed(agent.startedAt, now)})`
          ) : (
            <span className="absent">Não observado. Primeiro evento às {clock(agent.firstSeenAt)}</span>
          )}
        </Field>
        <Field label="Último evento">
          {clock(agent.lastEventAt)} (há {elapsed(agent.lastEventAt, now)})
        </Field>
        <Field label="Sessão">
          {agent.shortSession}
          {agent.project ? ` em ${agent.project}` : ''}
        </Field>
      </dl>

      <section>
        <h3>Ferramentas utilizadas</h3>
        {tools.length === 0 ? (
          <p className="absent">Nenhuma ferramenta registrada.</p>
        ) : (
          <ul className="tools">
            {tools.map(([tool, count]) => (
              <li key={tool}>
                {tool} <span>{count}×</span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <h3>Arquivos acessados</h3>
        {agent.files.length === 0 ? (
          <p className="absent">Nenhum arquivo informado pelos eventos.</p>
        ) : (
          <ul className="files">
            {agent.files.slice(0, 15).map((file) => (
              <li key={file}>{file}</li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <h3>Histórico recente</h3>
        <ol className="history">
          {events.map((event, index) => (
            <li key={`${event.occurredAt}-${index}`} className={event.type.endsWith('failed') ? 'history-error' : undefined}>
              <time>{clock(event.occurredAt)}</time>
              <span>{describeEvent(event)}</span>
            </li>
          ))}
        </ol>
        {agent.eventCount > events.length && (
          <p className="note">{agent.eventCount} eventos recebidos no total.</p>
        )}
      </section>
    </aside>
  );
}

function HooksDialog({ diagnostics, onClose }: { diagnostics: Diagnostics | null; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    ref.current?.showModal();
  }, []);
  const toolEvents = diagnostics ? diagnostics.toolEventsWithAgentId + diagnostics.toolEventsWithoutAgentId : 0;
  return (
    <dialog ref={ref} className="hooks-dialog" onClose={onClose}>
      <header className="panel-head">
        <h2>O que os hooks informam</h2>
        <button type="button" className="ghost" onClick={() => ref.current?.close()}>
          Fechar
        </button>
      </header>
      <p>
        Esta tela só mostra o que chegou pelos hooks do Claude Code. A tabela lista os campos realmente recebidos em cada
        tipo de evento, nesta máquina.
      </p>
      {diagnostics && diagnostics.total > 0 ? (
        <>
          <p>
            <strong>{diagnostics.total}</strong> eventos recebidos. Dos {toolEvents} eventos de ferramenta,{' '}
            <strong>{diagnostics.toolEventsWithAgentId}</strong> vieram com identificação de subagente e{' '}
            <strong>{diagnostics.toolEventsWithoutAgentId}</strong> sem ela (sessão principal).
          </p>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Evento</th>
                  <th>Vezes</th>
                  <th>Campos recebidos</th>
                </tr>
              </thead>
              <tbody>
                {diagnostics.hooks.map((hook) => (
                  <tr key={hook.hook}>
                    <td>{hook.hook}</td>
                    <td>{hook.count}</td>
                    <td>{hook.fields.join(', ')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      ) : (
        <p className="absent">Nenhum evento recebido ainda.</p>
      )}
      <h3>Limites desta versão</h3>
      <ul className="limits">
        <li>Instruções, comandos e saídas de ferramenta não são enviados à Central, por privacidade.</li>
        <li>
          Por isso a tarefa atual só aparece quando o agente registra uma tarefa ou quando o lançamento do subagente traz
          uma descrição ligada ao identificador dele.
        </li>
        <li>A sessão principal não tem nome nos hooks e aparece como "Sessão principal".</li>
        <li>Um subagente só recebe nome quando o hook informa o tipo dele.</li>
        <li>Um agente trabalhando sem nenhum evento há 10 minutos passa a "Desconhecido".</li>
      </ul>
    </dialog>
  );
}

export function App() {
  const stageRef = useRef<HTMLDivElement>(null);
  const sceneRef = useRef<OfficeScene | null>(null);
  const [agents, setAgents] = useState<Map<string, AgentView>>(new Map());
  const [diagnostics, setDiagnostics] = useState<Diagnostics | null>(null);
  const [connected, setConnected] = useState(false);
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [showHooks, setShowHooks] = useState(false);
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(
    () =>
      connectHub({
        onAgents: (next, diag) => {
          setAgents(next);
          setDiagnostics(diag);
        },
        onConnection: setConnected,
      }),
    [],
  );

  useEffect(() => {
    const parent = stageRef.current;
    if (!parent) return;
    let game: Phaser.Game | null = null;
    let cancelled = false;
    const start = () => {
      if (cancelled) return;
      const scene = new OfficeScene();
      scene.onSelect = setSelectedKey;
      sceneRef.current = scene;
      game = new Phaser.Game({
        type: Phaser.AUTO,
        parent,
        backgroundColor: '#14222a',
        pixelArt: true,
        roundPixels: true,
        banner: false,
        audio: { noAudio: true },
        scale: { mode: Phaser.Scale.RESIZE, width: parent.clientWidth, height: parent.clientHeight },
        scene,
      });
    };
    // A fonte dos rótulos precisa estar carregada antes de o Phaser desenhar texto.
    const fontReady = document.fonts?.load('11px "VT323"') ?? Promise.resolve([]);
    Promise.race([fontReady, new Promise((resolve) => window.setTimeout(resolve, 1500))]).then(start, start);
    return () => {
      cancelled = true;
      sceneRef.current = null;
      game?.destroy(true);
    };
  }, []);

  const visible = useMemo(() => {
    const list = [...agents.values()].filter(
      (agent) => agent.state !== 'disconnected' || now - Date.parse(agent.lastEventAt) < HIDE_DISCONNECTED_AFTER_MS,
    );
    list.sort((a, b) => Date.parse(b.lastEventAt) - Date.parse(a.lastEventAt));
    return list.slice(0, MAX_DESKS).sort((a, b) => Date.parse(a.firstSeenAt) - Date.parse(b.firstSeenAt));
    // "now" entra só a cada minuto, para não reordenar a cada segundo.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agents, Math.floor(now / 60000)]);

  useEffect(() => {
    sceneRef.current?.setAgents(visible);
  }, [visible]);

  useEffect(() => {
    sceneRef.current?.setSelected(selectedKey);
  }, [selectedKey]);

  const selected = selectedKey ? agents.get(selectedKey) ?? null : null;
  const parent = selected?.parentKey ? agents.get(selected.parentKey) ?? null : null;
  const hidden = agents.size - visible.length;
  const counts = STATE_ORDER.map((state) => ({ state, count: visible.filter((a) => a.state === state).length })).filter(
    (entry) => entry.count > 0,
  );

  const choose = (key: string) => {
    setSelectedKey(key);
    sceneRef.current?.focus(key);
  };

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <h1>Central dos Agentes</h1>
          <p>Terminal Pontual</p>
        </div>
        <div className="summary" aria-label="Resumo dos estados">
          {counts.map(({ state, count }) => (
            <span key={state} className={`count count-${state}`}>
              <strong>{count}</strong> {STATE_LABEL[state].toLowerCase()}
            </span>
          ))}
        </div>
        <div className="top-actions">
          <span className={connected ? 'link link-on' : 'link link-off'}>
            {connected ? 'Conectada ao serviço de eventos' : 'Sem conexão com o serviço de eventos'}
          </span>
          <button type="button" onClick={() => setShowHooks(true)}>
            O que os hooks informam
          </button>
        </div>
      </header>

      <main className={connected ? 'stage' : 'stage stage-offline'}>
        <div className="canvas" ref={stageRef} />
        {connected && visible.length === 0 && (
          <div className="empty">
            <h2>O escritório está vazio</h2>
            <p>
              Nenhum evento chegou ainda. Use o Claude Code neste projeto e os agentes aparecem aqui assim que os hooks
              dispararem.
            </p>
          </div>
        )}
        {!connected && (
          <div className="empty">
            <h2>Sem conexão com o serviço de eventos</h2>
            <p>O que está na tela pode estar desatualizado. Inicie o serviço com "npm start" em src/agenthub.</p>
          </div>
        )}
        <p className="hint">Arraste para mover, use a roda do mouse para aproximar, clique em um agente para ver os detalhes.</p>
        {selected && <AgentPanel agent={selected} parent={parent} now={now} onClose={() => setSelectedKey(null)} />}
      </main>

      <footer className="dock" aria-label="Todos os agentes">
        <div className="dock-head">
          <h2>Todos os agentes</h2>
          {hidden > 0 && (
            <span className="note">
              Mostrando {visible.length} de {agents.size}. Os demais estão desconectados há mais de duas horas ou
              excedem as {MAX_DESKS} mesas.
            </span>
          )}
        </div>
        {visible.length === 0 ? (
          <p className="absent">Nenhum agente observado.</p>
        ) : (
          <ul className="roster">
            {visible.map((agent) => (
              <li key={agent.key}>
                <button
                  type="button"
                  className={agent.key === selectedKey ? 'card card-selected' : 'card'}
                  onClick={() => choose(agent.key)}
                >
                  <span className="card-top">
                    <span className="card-name">
                      {agent.kind === 'main' && <span className="star" aria-hidden="true">★</span>}
                      {agent.nickname}
                    </span>
                    <StateChip state={agent.state} />
                  </span>
                  <span className="card-kind">
                    {agent.name}, {agent.kind === 'main' ? 'agente principal' : 'subagente'}
                  </span>
                  <span className="card-activity">{activityLine(agent)}</span>
                  <span className="card-time">
                    {agent.startedAt ? `Iniciou às ${clock(agent.startedAt)}` : `Visto desde ${clock(agent.firstSeenAt)}`}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </footer>

      {showHooks && <HooksDialog diagnostics={diagnostics} onClose={() => setShowHooks(false)} />}
    </div>
  );
}
