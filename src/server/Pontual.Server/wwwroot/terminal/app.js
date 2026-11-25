// Tela da atendente do Terminal Pontual (docs/V1-CONTRATO.md, seções 5 e 6).
// Funciona em dois ambientes:
//  - dentro do programa Terminal, que injeta window.PONTUAL = { token, hostname, hosted: true },
//    abre os sites e envia heartbeat e eventos;
//  - dentro do programa Terminal em modo local (sem servidor), que injeta
//    window.PONTUAL = { hosted: true, local: true, hostname, config }: a página não chama nenhuma API;
//  - num navegador comum, com ?token= na URL: a própria página simula tudo isso, para teste.
// Regra da casa: nenhum dado vindo do servidor entra na página por innerHTML, só por textContent.
(() => {
  'use strict';

  // ================================================================= ícones
  // Desenhos fixos, escritos aqui mesmo. É o único uso de innerHTML do arquivo.
  const IC = {
    search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>', x: '<path d="M6 6l12 12M18 6 6 18"/>',
    check: '<path d="m5 12 5 5 9-10"/>', arrow: '<path d="M5 12h14M13 6l6 6-6 6"/>',
    alert: '<path d="M12 4 3 20h18L12 4Z"/><path d="M12 10v4M12 17h.01"/>',
    headset: '<path d="M4 14v-2a8 8 0 0 1 16 0v2"/><rect x="3" y="13" width="4" height="6" rx="1.5"/><rect x="17" y="13" width="4" height="6" rx="1.5"/><path d="M19 19c0 1.5-2 2-5 2"/>',
    wrench: '<path d="M14.5 6.5a4 4 0 0 0-5.3 5.3L4 17l3 3 5.2-5.2a4 4 0 0 0 5.3-5.3l-2.6 2.6-2-2 2.6-2.6Z"/>',
    ban: '<circle cx="12" cy="12" r="9"/><path d="m6 6 12 12"/>', clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    pdf: '<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8l-5-5Z"/><path d="M14 3v5h5M9 13h6M9 17h4"/>',
    nota: '<path d="M5 4h14v10l-5 6H5V4Z"/><path d="M14 20v-6h5M8.5 8.5h7M8.5 12h4"/>',
    calc: '<rect x="5" y="3" width="14" height="18" rx="2"/><path d="M8 7h8M8 12h.01M12 12h.01M16 12h.01M8 16h.01M12 16h.01M16 16h.01"/>',
    grid: '<rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/>',
    lock: '<rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
    power: '<path d="M12 4v8"/><path d="M7 7a7 7 0 1 0 10 0"/>', folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z"/>', load: '<path d="M12 3a9 9 0 1 0 9 9"/>',
  };
  function ic(nome, tam = 18) {
    const s = document.createElement('span');
    s.innerHTML = `<svg width="${Number(tam)}" height="${Number(tam)}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${IC[nome]}</svg>`;
    return s.firstChild;
  }
  // Marcadores data-ic viram ícones: dentro do elemento, se for <i>; no lugar dele, se for <span>.
  for (const alvo of document.querySelectorAll('[data-ic]')) {
    const icone = ic(alvo.dataset.ic, Number(alvo.dataset.tam) || 18);
    if (alvo.tagName === 'I') alvo.replaceChildren(icone); else alvo.replaceWith(icone);
  }

  // ============================================================== utilidades
  const CORES = ['#0d47a1', '#1976d2', '#2e9e5b', '#0f9d76', '#0c8fd0', '#2e416d', '#4f6fcf', '#0f766e'];
  const GRUPOS = ['Bancos e promotoras', 'Comunicação', 'Pontual e ferramentas'];
  const $ = (id) => document.getElementById(id);
  const el = (tag, props = {}, ...filhos) => { const n = Object.assign(document.createElement(tag), props); n.append(...filhos.filter((f) => f !== null && f !== undefined)); return n; };
  const hhmm = (d = new Date()) => d.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
  const cor = (nome) => { let h = 0; for (const c of nome) h = (h * 31 + c.charCodeAt(0)) >>> 0; return CORES[h % CORES.length]; };
  const iniciais = (nome) => {
    const p = nome.trim().split(/\s+/).filter(Boolean);
    if (!p.length) return '?';
    return ((p[0][0] || '') + (p[1] ? p[1][0] : p[0][1] || '')).toUpperCase();
  };
  const avatar = (nome, classe = '') => {
    const a = el('span', { className: `avatar ${classe}`, textContent: iniciais(nome) });
    a.style.background = cor(nome);
    a.setAttribute('aria-hidden', 'true');
    return a;
  };
  // Logos dos sites (pelo domínio) e programas (pelo nome do .exe): logos/mapa.json. Sem logo, ficam as iniciais.
  let LOGOS = { hosts: {}, exes: {} };
  fetch('logos/mapa.json').then((r) => (r.ok ? r.json() : null)).then((m) => { if (m) { LOGOS = m; if (!modoCalc) desenhar(); } }).catch(() => {});
  const logoDoExe = (caminho) => LOGOS.exes[String(caminho || '').split(/[\\/]/).pop().toLowerCase()];
  const logoDe = (i) => {
    if (!i) return undefined;
    if (i.kind === 'program') return logoDoExe(i.target);
    let h = '';
    try { h = new URL(i.target).hostname; } catch { return undefined; }
    for (; h; h = h.includes('.') ? h.slice(h.indexOf('.') + 1) : '') if (LOGOS.hosts[h]) return LOGOS.hosts[h];
    return undefined;
  };
  const marca = (nome, logo, classe) => {
    const a = avatar(nome, classe);
    if (!logo) return a;
    const img = el('img', { alt: '', src: `logos/${logo}.svg` });
    img.onerror = () => a.replaceWith(avatar(nome, classe));
    a.classList.add('logo-marca'); a.style.background = ''; a.replaceChildren(img);
    return a;
  };
  const primeiro = (nome) => nome.trim().split(/\s+/)[0] || '';
  const saudar = () => { const h = new Date().getHours(); return h < 12 ? 'Bom dia' : h < 18 ? 'Boa tarde' : 'Boa noite'; };
  const texto = (v, max = 300) => (typeof v === 'string' ? v.slice(0, max) : '');
  const semAcento = (s) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();

  // ================================================================ ambiente
  const parametros = new URLSearchParams(location.search);
  const injetado = window.PONTUAL && typeof window.PONTUAL === 'object' ? window.PONTUAL : null;
  const P = injetado && injetado.hosted === true
    ? { token: texto(injetado.token, 500), hostname: texto(injetado.hostname, 100), hosted: true, local: injetado.local === true, locked: injetado.locked === true, apps: injetado.apps === true, calc: injetado.calc === true, pdf: injetado.pdf === true, notes: injetado.notes === true }
    : { token: parametros.get('token') || '', hostname: parametros.get('hostname') || 'Navegador', hosted: false, local: false };
  // Modo janela da calculadora: o Terminal (2.11+) abre esta mesma página numa janela própria do Windows com ?calc=1.
  const modoCalc = parametros.get('calc') === '1';
  const ponte = P.hosted && window.chrome && window.chrome.webview ? window.chrome.webview : null;
  const aoPrograma = (mensagem) => { if (ponte) ponte.postMessage(mensagem); };

  const INTERVALO_SINAL = 15000;   // heartbeat, quando a página não está hospedada
  const INTERVALO_NOVA_TENTATIVA = 5000;
  const CHAVE_USO = 'pontual.terminal.uso';
  const INICIO_SESSAO = new Date().toISOString();

  // ================================================================== estado
  const S = {
    config: null,            // última configuração conhecida: { version, attendantName, observationMode, items }
    invalido: false,         // 401: token ausente, inválido ou máquina removida
    estado: null,            // estado informado pelo servidor: 'pending', 'active' ou 'maintenance'
    manut: null,             // { reason, until }
    online: null,            // null enquanto não se sabe
    nome: '',                // nome da atendente
    abertos: [],             // [{ id, name }]
    cat: 'Todos',
    avisoAberto: false,
    apps: null,              // aplicativos do computador, informados pelo programa: [{ id, name, exe, source, status, open }]
    appsAberto: false,
    appsBusca: '',
    calcAberto: false,
    calc: { expr: '', ultima: '' }, // calculadora própria do Terminal
    uso: lerUso(),
  };

  function lerUso() {
    try {
      const bruto = JSON.parse(localStorage.getItem(CHAVE_USO) || '{}');
      const uso = {};
      if (bruto && typeof bruto === 'object') for (const [id, n] of Object.entries(bruto)) if (Number.isFinite(n) && n > 0) uso[id] = n;
      return uso;
    } catch { return {}; }
  }
  function contarUso(id) {
    S.uso[id] = (S.uso[id] || 0) + 1;
    try { localStorage.setItem(CHAVE_USO, JSON.stringify(S.uso)); } catch { /* sem armazenamento: a contagem vale só nesta sessão */ }
  }

  // ===================================================================== API
  class ErroHttp extends Error { constructor(status) { super(`HTTP ${status}`); this.status = status; } }
  async function api(metodo, caminho, corpo) {
    const r = await fetch(`/api/v1${caminho}`, {
      method: metodo, cache: 'no-store', credentials: 'omit',
      headers: { Authorization: `Bearer ${P.token}`, ...(corpo ? { 'Content-Type': 'application/json' } : {}) },
      body: corpo ? JSON.stringify(corpo) : undefined,
      signal: AbortSignal.timeout(10000),
    });
    if (!r.ok) throw new ErroHttp(r.status);
    return r.status === 204 ? null : r.json();
  }
  // Falha de rede (servidor fora do ar, cabo solto) é diferente de resposta de erro do servidor.
  const falhaDeRede = (e) => !(e instanceof ErroHttp);

  function lerConfig(c) {
    const itens = Array.isArray(c && c.items) ? c.items : [];
    return {
      version: Number.isFinite(c && c.version) ? c.version : 0,
      attendantName: texto(c && c.attendantName, 120),
      observationMode: Boolean(c && c.observationMode),
      items: itens.filter((i) => i && typeof i.id === 'string' && typeof i.name === 'string').map((i) => ({
        id: i.id, name: texto(i.name, 120), kind: i.kind === 'program' ? 'program' : 'site', target: texto(i.target, 2000),
        group: texto(i.group, 80) || GRUPOS[2], hosts: Array.isArray(i.hosts) ? i.hosts.filter((h) => typeof h === 'string') : [],
      })),
    };
  }

  let buscandoConfig = false, tentarDeNovo = 0;
  async function carregarConfig() {
    if (P.local || buscandoConfig || S.invalido) return;
    buscandoConfig = true;
    clearTimeout(tentarDeNovo);
    try {
      S.config = lerConfig(await api('GET', '/device/config'));
      S.online = true;
      if (S.config.attendantName) S.nome = S.config.attendantName;
      if (!S.estado) S.estado = 'active';
    } catch (e) {
      if (e.status === 401) S.invalido = true;
      else {
        if (falhaDeRede(e)) S.online = false;
        // Máquina ainda pendente pode receber erro aqui; o estado vem pelo heartbeat. Tenta de novo logo.
        if (S.estado !== 'pending') tentarDeNovo = setTimeout(carregarConfig, INTERVALO_NOVA_TENTATIVA);
      }
    } finally {
      buscandoConfig = false;
      desenhar();
    }
  }

  function aplicarEstado(r) {
    const antes = S.estado;
    if (r.state === 'pending' || r.state === 'active' || r.state === 'maintenance') S.estado = r.state;
    if (typeof r.attendantName === 'string' && r.attendantName) S.nome = texto(r.attendantName, 120);
    S.manut = r.maintenance && typeof r.maintenance === 'object'
      ? { reason: texto(r.maintenance.reason, 300), until: texto(r.maintenance.until, 40) } : null;
    // Saiu de "pendente": a lista passa a existir.
    if (antes === 'pending' && S.estado !== 'pending') carregarConfig();
  }

  // ============================================ simulação (fora do programa)
  const janelas = new Map();   // id do item -> janela do navegador
  const fila = [];             // eventos ainda não entregues
  let enviandoFila = false;

  function evento(type, item, detail) {
    fila.push({ type, ...(item ? { item } : {}), ...(detail ? { detail } : {}), at: new Date().toISOString() });
    if (fila.length > 500) fila.splice(0, fila.length - 500);
    enviarFila();
  }
  async function enviarFila() {
    if (enviandoFila || !fila.length || S.invalido) return;
    enviandoFila = true;
    const lote = fila.slice(0, 50);
    try {
      await api('POST', '/device/events', { events: lote });
      fila.splice(0, lote.length);
    } catch (e) {
      if (e.status === 401) S.invalido = true;
      // Erro de rede: fica na fila. Recusa do servidor (400, por exemplo): descarta, para não repetir para sempre.
      else if (!falhaDeRede(e)) fila.splice(0, lote.length);
    } finally {
      enviandoFila = false;
    }
    if (fila.length && S.online) enviarFila();
  }

  async function sinal() {
    if (S.invalido) return;
    try {
      const r = await api('POST', '/device/heartbeat', { appsInUse: S.abertos.map((a) => a.name), sessionStartedAt: INICIO_SESSAO });
      S.online = true;
      aplicarEstado(r || {});
      const versao = Number.isFinite(r && r.configVersion) ? r.configVersion : null;
      if (S.estado !== 'pending' && (!S.config || (versao !== null && versao !== S.config.version))) carregarConfig();
      enviarFila();
    } catch (e) {
      if (e.status === 401) S.invalido = true;
      else if (falhaDeRede(e)) S.online = false;
    }
    desenhar();
  }

  function fecharSimulado(id, fecharJanela) {
    const aberto = S.abertos.find((a) => a.id === id);
    const janela = janelas.get(id);
    janelas.delete(id);
    if (fecharJanela && janela && !janela.closed) try { janela.close(); } catch { /* a janela é de outro site */ }
    if (!aberto) return;
    S.abertos = S.abertos.filter((a) => a.id !== id);
    evento('closed', aberto.name);
    desenhar();
  }
  function vigiarJanelas() {
    for (const [id, janela] of [...janelas]) if (!janela || janela.closed) fecharSimulado(id, false);
    // Item que deixou de ser permitido é fechado.
    if (S.config) for (const a of [...S.abertos]) if (!S.config.items.some((i) => i.id === a.id)) fecharSimulado(a.id, true);
  }
  function abrirSimulado(item) {
    if (item.kind === 'program') { avisar('Programas só abrem pelo Terminal instalado no computador.', true); return; }
    let url;
    try { url = new URL(item.target); } catch { url = null; }
    if (!url || (url.protocol !== 'http:' && url.protocol !== 'https:')) { avisar('O endereço deste site está incorreto. Chame o TI.', true); return; }
    const existente = janelas.get(item.id);
    if (existente && !existente.closed) { existente.focus(); return; }
    const janela = window.open(url.href, '_blank');
    if (!janela) { avisar('O navegador bloqueou a nova aba. Permita pop-ups para testar.', true); return; }
    try { janela.opener = null; } catch { /* já isolada */ }
    janelas.set(item.id, janela);
    if (!S.abertos.some((a) => a.id === item.id)) { S.abertos.push({ id: item.id, name: item.name }); evento('opened', item.name); }
  }

  // ================================================================== ações
  function abrir(item) {
    contarUso(item.id);
    if (P.hosted) aoPrograma({ type: 'open', id: item.id }); else abrirSimulado(item);
    desenhar();
  }
  function focar(aberto) {
    if (P.hosted) { aoPrograma({ type: 'focus', id: aberto.id }); return; }
    const janela = janelas.get(aberto.id);
    if (janela && !janela.closed) janela.focus();
  }
  function fechar(aberto) {
    if (P.hosted) aoPrograma({ type: 'close', id: aberto.id }); else fecharSimulado(aberto.id, true);
  }

  function avisar(mensagem, problema = false) {
    const caixa = el('div', { className: problema ? 'toast urgente' : 'toast' }, ic(problema ? 'alert' : 'check', 20), el('div', { textContent: mensagem }));
    $('avisos').append(caixa);
    setTimeout(() => caixa.remove(), 7000);
  }
  function chamarTi() {
    const botao = $('at-chamar');
    if (botao.disabled) return;
    // "help" é um evento: quem envia é o programa, quando hospedada; a página, quando não.
    if (P.hosted) aoPrograma({ type: 'help' }); else evento('help');
    if (P.local) avisar('Este computador está em modo local, sem servidor configurado. O chamado ficou só registrado aqui: avise o TI de outra forma.', true);
    else avisar(S.online === false
      ? 'Sem conexão agora. O chamado vai ser enviado ao TI assim que a conexão voltar.'
      : 'Chamado enviado. O TI já foi avisado.', S.online === false);
    // Pausa curta para um clique repetido não virar vários chamados.
    botao.disabled = true;
    setTimeout(() => { botao.disabled = false; }, 8000);
  }

  // ================================================================= desenho
  function relogio() {
    const d = new Date();
    for (const t of document.querySelectorAll('[data-hora]')) t.textContent = hhmm(d);
    for (const t of document.querySelectorAll('[data-data]')) t.textContent = d.toLocaleDateString('pt-BR', { weekday: 'short', day: 'numeric', month: 'short' });
  }

  function previsao(ate) {
    const d = new Date(ate);
    if (!ate || Number.isNaN(d.getTime())) return '';
    const hoje = new Date().toDateString() === d.toDateString();
    return hoje ? hhmm(d) : `${d.toLocaleDateString('pt-BR', { day: 'numeric', month: 'short' })}, ${hhmm(d)}`;
  }

  function desenharTopo() {
    const ligacao = $('ligacao');
    ligacao.textContent = P.local ? 'Modo local' : S.online === null ? 'Conectando' : S.online ? 'Conectado' : 'Sem conexão';
    ligacao.className = `ligado${P.local || S.online === null ? ' espera' : S.online ? '' : ' sem'}`;
    const quem = S.nome || P.hostname;
    $('eu').hidden = !quem;
    if (quem) {
      const novo = avatar(quem);
      novo.id = 'eu-avatar';
      $('eu-avatar').replaceWith(novo);
      $('eu-nome').textContent = quem;
      $('eu-pc').textContent = S.nome ? P.hostname : '';
    }
  }

  // Tela de estado (no lugar da lista): carregando, pendente, cadastro inválido, sem conexão e sem lista.
  function estadoDaTela() {
    if (S.invalido) {
      return { icone: 'ban', tom: 'rubi', titulo: 'Este computador não está cadastrado',
        partes: [P.hosted || P.token ? 'O cadastro deste computador não vale mais. Chame o TI para cadastrar de novo.'
          : 'Abra esta tela pelo programa Terminal Pontual. Para testar no navegador, informe ?token= no endereço.'] };
    }
    if (S.estado === 'pending') {
      return { icone: 'clock', tom: 'ambar', titulo: 'Este computador está aguardando a aprovação do TI',
        partes: ['O computador ', el('strong', { textContent: P.hostname }), ' já foi cadastrado. Assim que o TI aprovar, esta tela muda sozinha.'] };
    }
    if (!S.config) {
      if (S.online === false) return { icone: 'power', tom: 'rubi', titulo: 'Sem conexão', partes: ['Não foi possível falar com o servidor da Pontual. A tela tenta de novo sozinha a cada poucos segundos.'] };
      return { icone: 'load', tom: '', girando: true, titulo: 'Carregando', partes: ['Buscando o que está liberado para você.'] };
    }
    return null;
  }

  let ultimoEstado = '';
  function desenhar() {
    desenharTopo();
    const tela = estadoDaTela();
    $('at-estado').hidden = !tela;
    $('at-inicio').hidden = Boolean(tela);
    $('rodape').hidden = Boolean(tela);
    if (tela) {
      // Só redesenha quando muda, para o leitor de tela não repetir o mesmo aviso a cada consulta.
      const chave = `${tela.titulo}|${P.hostname}`;
      if (chave !== ultimoEstado) {
        const icone = $('estado-icone');
        icone.className = `${tela.tom}${tela.girando ? ' girando' : ''}`;
        icone.replaceChildren(ic(tela.icone, 44));
        $('estado-titulo').textContent = tela.titulo;
        $('estado-texto').replaceChildren(...tela.partes);
        ultimoEstado = chave;
      }
    } else {
      ultimoEstado = '';
      desenharInicio();
    }
    desenharCamadas();
  }

  function desenharSaudacao() {
    const nome = primeiro(S.nome);
    $('saudacao').replaceChildren(...(nome ? [`${saudar()}, `, el('span', { textContent: nome }), '.'] : [el('span', { textContent: saudar() }), '.']));
  }

  function desenharInicio() {
    const itens = S.config.items;
    desenharSaudacao();
    $('saudacao-sub').textContent = 'O que você vai abrir agora?';

    const termo = semAcento($('busca').value.trim());
    const dominio = (i) => (i.kind === 'program' ? '' : i.hosts[0] || i.target.replace(/^https?:\/\//i, '').split('/')[0]);
    const casa = (i) => !termo || semAcento(i.name).includes(termo) || i.hosts.some((h) => h.toLowerCase().includes(termo)) || dominio(i).toLowerCase().includes(termo);

    $('sec-abertos').hidden = S.abertos.length === 0 || Boolean(termo);
    $('abertos').replaceChildren(...S.abertos.map((a) => {
      const ir = el('button', { type: 'button' }, marca(a.name, logoDe(S.config && S.config.items.find((i) => i.id === a.id)), 'm q'), a.name);
      ir.setAttribute('aria-label', `Voltar para ${a.name}`);
      ir.onclick = () => focar(a);
      const x = el('button', { type: 'button', className: 'x' }, ic('x', 16));
      x.setAttribute('aria-label', `Fechar ${a.name}`);
      x.onclick = () => fechar(a);
      return el('div', { className: 'aberto' }, ir, x);
    }));

    const destaques = itens.filter((i) => S.uso[i.id]).sort((a, b) => S.uso[b.id] - S.uso[a.id]).slice(0, 6);
    $('sec-destaques').hidden = Boolean(termo) || destaques.length === 0;
    $('destaques').replaceChildren(...destaques.map((i) => {
      const b = el('button', { type: 'button', className: 'destaque' }, marca(i.name, logoDe(i), 'gg'),
        el('span', {}, el('b', { textContent: i.name }), el('small', { textContent: i.kind === 'program' ? 'Programa' : i.group })));
      b.onclick = () => abrir(i);
      return b;
    }));

    const grupos = ['Todos', ...GRUPOS, ...new Set(itens.map((i) => i.group).filter((g) => !GRUPOS.includes(g)))];
    if (!grupos.includes(S.cat)) S.cat = 'Todos';
    const guiaComFoco = document.activeElement && document.activeElement.parentElement === $('guias-grupo');
    $('guias-grupo').replaceChildren(...grupos.map((g) => {
      const b = el('button', { type: 'button', textContent: g });
      b.setAttribute('role', 'tab'); b.setAttribute('aria-selected', String(S.cat === g));
      b.onclick = () => { S.cat = g; desenhar(); };
      return b;
    }));
    if (guiaComFoco) { const atual = $('guias-grupo').querySelector('[aria-selected="true"]'); if (atual) atual.focus(); }

    const lista = itens.filter((i) => (S.cat === 'Todos' || i.group === S.cat) && casa(i));
    $('todos-titulo').textContent = termo ? `${lista.length} ${lista.length === 1 ? 'resultado' : 'resultados'}` : `Tudo o que está liberado para você (${itens.length})`;
    $('lista-sites').replaceChildren(...lista.map((i) => {
      const b = el('button', { type: 'button', className: 'item' }, marca(i.name, logoDe(i), 'q'),
        el('span', {}, el('b', { textContent: i.name }), el('small', { textContent: i.kind === 'program' ? 'Programa instalado' : dominio(i) })), ic('arrow', 20));
      b.onclick = () => abrir(i);
      return b;
    }));
    $('nada').hidden = lista.length > 0;
    $('nada').textContent = itens.length === 0
      ? 'Nenhum site ou programa foi liberado para este computador ainda. Se precisar de algum, chame o TI.'
      : termo ? 'Nada encontrado com esse nome. Se precisar de um site que não está aqui, chame o TI.'
        : 'Nada liberado neste grupo.';
    $('busca').onkeydown = (e) => { if (e.key === 'Enter' && lista.length) { $('busca').value = ''; abrir(lista[0]); } };
    $('at-config').textContent = `Configuração ${S.config.version}`;
  }

  // Manutenção (tela cheia) e a folha "O que é registrado".
  let focoAntes = null;
  function desenharCamadas() {
    const emManutencao = S.estado === 'maintenance' && !S.invalido;
    const manut = $('at-manut');
    const estavaEscondida = manut.hidden;
    manut.hidden = !emManutencao;
    if (emManutencao) {
      const motivo = (S.manut && S.manut.reason ? S.manut.reason : '').trim().replace(/\.+$/, '');
      const ate = previsao(S.manut && S.manut.until);
      $('at-manut-texto').textContent = `O TI está trabalhando nesta máquina.${motivo ? ` Motivo: ${motivo}.` : ''}${ate ? ` Previsão de retorno: ${ate}.` : ''}`;
      S.avisoAberto = false;
      S.appsAberto = false;
      S.calcAberto = false;
    }

    const veu = $('at-veu'), folha = $('at-folha');
    const folhaAberta = S.avisoAberto || S.appsAberto || S.calcAberto;
    const estavaFechada = veu.hidden;
    veu.hidden = !folhaAberta;
    folha.classList.toggle('larga', S.appsAberto);
    folha.classList.toggle('calc', S.calcAberto);
    for (const parte of document.querySelectorAll('#app-at > header, #app-at > main, #app-at > footer')) parte.inert = emManutencao || folhaAberta;

    if (emManutencao && estavaEscondida) manut.focus();
    if (S.appsAberto) desenharAplicativos(folha, estavaFechada);
    if (S.calcAberto) desenharCalculadora(folha, estavaFechada);
    if (S.avisoAberto && estavaFechada) {
      focoAntes = document.activeElement;
      const ok = el('button', { type: 'button', className: 'btn', textContent: 'Entendi' });
      ok.onclick = fecharAviso;
      const lista = (linhas) => el('ul', {}, ...linhas.map((t) => el('li', { textContent: t })));
      folha.setAttribute('aria-labelledby', 'aviso-titulo');
      folha.replaceChildren(el('h2', { id: 'aviso-titulo', textContent: 'O que é registrado neste computador' }),
        el('h3', { textContent: 'A Pontual registra' }),
        lista(['Horários em que o computador está ligado e conectado.', 'As notas adesivas que você escreve no Terminal: ficam guardadas na Central, e o TI pode ler.', 'Quais programas e sites autorizados você abriu, e por quanto tempo.', 'Tentativas de abrir um programa ou site bloqueado.',
          'O endereço (só o domínio) dos sites fora da lista que foram abertos, e os chamados feitos ao TI.']),
        el('h3', { textContent: 'O TI pode ver a sua tela ao vivo' }),
        lista(['Quando precisar, o TI pode abrir a visualização da tela deste computador, incluindo os dois monitores.',
          'A imagem é só ao vivo: não é gravada nem guardada. Fica registrado quem abriu a visualização e quando.']),
        el('h3', { textContent: 'A Pontual não registra' }),
        lista(['O que você digita.', 'Gravações da sua tela.', 'O conteúdo de mensagens, documentos e páginas.', 'Áudio, câmera ou localização.']),
        el('div', { className: 'acoes' }, ok));
      ok.focus();
    }
    if (!folhaAberta && !estavaFechada && focoAntes && document.contains(focoAntes) && !emManutencao) { focoAntes.focus(); focoAntes = null; }
  }
  function fecharAviso() { S.avisoAberto = false; desenhar(); }

  // Folha "Aplicativos do computador": a lista vem do programa (mensagem "apps"); abrir é pedido ao programa ("open-app").
  const ORIGENS_APP = [['windows', 'Do Windows'], ['store', 'Microsoft Store'], ['installed', 'Instalados neste computador'], ['downloaded', 'Baixados (pasta Downloads)']];
  const SITUACAO_APP = { blocked: 'Bloqueado pelo TI', allowed: 'Liberado pelo TI', request: 'Na primeira vez, o TI recebe um pedido para liberar' };
  let appsAssinatura = '';
  function desenharAplicativos(folha, primeira) {
    const termo = semAcento(S.appsBusca.trim());
    const lista = (S.apps || []).filter((a) => !termo || semAcento(a.name).includes(termo) || a.exe.includes(termo));
    const assinatura = JSON.stringify([S.apps, termo]);
    if (!primeira && assinatura === appsAssinatura) return;
    appsAssinatura = assinatura;
    const buscaTinhaFoco = document.activeElement && document.activeElement.id === 'apps-busca';
    const busca = el('input', { id: 'apps-busca', type: 'search', className: 'procura-apps', autocomplete: 'off', spellcheck: false, placeholder: 'Digite o nome do aplicativo', value: S.appsBusca });
    busca.setAttribute('aria-label', 'Buscar aplicativo');
    busca.oninput = () => { S.appsBusca = busca.value; desenharAplicativos(folha, false); };
    const fechar = el('button', { type: 'button', className: 'btn claro', textContent: 'Fechar' });
    fechar.onclick = fecharAplicativos;
    const grupos = ORIGENS_APP.map(([origem, titulo]) => [titulo, lista.filter((a) => a.source === origem)]).filter(([, itens]) => itens.length);
    const corpo = S.apps === null ? el('p', { className: 'vazio', textContent: 'Procurando os aplicativos deste computador…' })
      : !lista.length ? el('p', { className: 'vazio', textContent: S.apps.length ? 'Nenhum aplicativo com esse nome.' : 'Nenhum aplicativo encontrado neste computador.' })
        : el('div', {}, ...grupos.flatMap(([titulo, itens]) => [el('h3', { textContent: titulo }), el('div', { className: 'lista-sites' }, ...itens.map((a) => {
          const bloqueado = a.status === 'blocked';
          const b = el('button', { type: 'button', className: 'item', disabled: bloqueado }, marca(a.name, logoDoExe(a.exe), 'q'),
            el('span', {}, el('b', { textContent: a.name }), el('small', { textContent: a.open ? 'Aberto agora' : SITUACAO_APP[a.status] || a.exe })), ic(bloqueado ? 'lock' : 'arrow', 20));
          b.title = a.exe;
          b.onclick = () => { aoPrograma({ type: 'open-app', id: a.id }); fecharAplicativos(); };
          return b;
        }))]));
    folha.setAttribute('aria-labelledby', 'apps-titulo');
    folha.replaceChildren(el('h2', { id: 'apps-titulo', textContent: 'Aplicativos do computador' }),
      el('p', { className: 'explica', textContent: 'Você pode abrir os aplicativos do Windows, os instalados e os que você baixou. Na primeira vez que abrir um aplicativo novo, o TI recebe um pedido para liberá-lo. Se o TI bloquear, ele deixa de abrir.' }),
      busca, corpo, el('div', { className: 'acoes' }, fechar));
    if (primeira || buscaTinhaFoco) { busca.focus(); busca.setSelectionRange(busca.value.length, busca.value.length); }
  }
  function abrirAplicativos() {
    if (S.appsAberto) return;
    S.appsAberto = true; S.apps = null; S.appsBusca = '';
    aoPrograma({ type: 'apps' });
    desenhar();
  }
  function fecharAplicativos() { S.appsAberto = false; desenhar(); }

  // Calculadora própria do Terminal (pedido do dono, 20/11/2026): a da Loja do Windows não abre na conta bloqueada.
  // Quatro operações e porcentagem, com a vírgula como decimal. Sem eval: a conta é lida por um analisador próprio.
  const TECLAS_CALC = [['C', 'apaga'], ['⌫', 'apaga'], ['%', 'op'], ['÷', 'op'], ['7'], ['8'], ['9'], ['×', 'op'], ['4'], ['5'], ['6'], ['-', 'op'], ['1'], ['2'], ['3'], ['+', 'op'], ['00'], ['0'], [','], ['=', 'igual']];
  function avaliarCalc(texto) {
    const t = texto.replace(/\s+/g, '').replace(/,/g, '.');
    if (!t) return null;
    let i = 0;
    const falha = () => { throw new Error('conta inválida'); };
    function numero() {
      let sinal = 1;
      while (t[i] === '-' || t[i] === '+') { if (t[i] === '-') sinal = -sinal; i++; }
      const m = /^(\d+\.?\d*|\.\d+)%?/.exec(t.slice(i));
      if (!m) falha();
      i += m[0].length;
      const v = parseFloat(m[0]);
      return sinal * (m[0].endsWith('%') ? v / 100 : v);
    }
    function termo() { let v = numero(); while (t[i] === '×' || t[i] === '÷') { const op = t[i++]; const d = numero(); v = op === '×' ? v * d : v / d; } return v; }
    function soma() { let v = termo(); while (t[i] === '+' || t[i] === '-') { const op = t[i++]; const d = termo(); v = op === '+' ? v + d : v - d; } return v; }
    const v = soma();
    if (i !== t.length || !Number.isFinite(v)) falha();
    return v;
  }
  const formatarCalc = (v) => Number(v.toPrecision(12)).toLocaleString('pt-BR', { maximumFractionDigits: 10, useGrouping: false });
  function digitarCalc(tecla) {
    const c = S.calc;
    const ultimo = c.expr.slice(-1), operador = (x) => '+-×÷'.includes(x) && x !== '';
    if (tecla === 'C') { c.expr = ''; }
    else if (tecla === '⌫') { c.expr = c.expr.slice(0, -1); }
    else if (tecla === '=') {
      try { const v = avaliarCalc(c.expr); if (v !== null) { c.ultima = `${c.expr} = ${formatarCalc(v)}`; c.expr = formatarCalc(v); } } catch { /* conta incompleta: fica como está */ }
    } else if (operador(tecla)) {
      if (!c.expr && tecla !== '-') return;
      if (operador(ultimo)) c.expr = c.expr.slice(0, -1);
      c.expr += tecla;
    } else if (tecla === '%') { if (/\d$/.test(ultimo)) c.expr += '%'; }
    else if (tecla === ',') { const atual = c.expr.split(/[+\-×÷]/).pop(); if (!atual.includes(',') && !atual.includes('%')) c.expr += (/\d$/.test(ultimo) ? '' : '0') + ','; }
    else if (/^\d+$/.test(tecla)) { if (ultimo === '%') return; if (c.expr.length < 60) c.expr += tecla; }
    if (modoCalc) desenharCalculadora($('calc-janela'), false); else desenhar();
  }
  function teclaCalculadora(e) {
    const mapa = { Enter: '=', Backspace: '⌫', Delete: 'C', Escape: null, '*': '×', x: '×', '/': '÷', '.': ',', ',': ',', '+': '+', '-': '-', '%': '%', '=': '=' };
    if (e.key === 'Escape') { fecharCalculadora(); return; }
    const tecla = /^\d$/.test(e.key) ? e.key : mapa[e.key];
    if (tecla === undefined) return;
    e.preventDefault();
    digitarCalc(tecla);
  }
  function desenharCalculadora(folha, primeira) {
    const c = S.calc;
    let previa = '', erro = false;
    try { const v = avaliarCalc(c.expr); previa = v === null ? '' : formatarCalc(v); } catch { erro = /[+\-×÷]$|^$/.test(c.expr) ? false : true; }
    if (!primeira && $('calc-expr')) {
      $('calc-ultima').textContent = c.ultima;
      $('calc-expr').textContent = c.expr || '0';
      const res = $('calc-res'); res.textContent = erro ? 'Conta incompleta' : previa; res.classList.toggle('erro', erro);
      return;
    }
    const fechar = el('button', { type: 'button', className: 'btn claro', textContent: 'Fechar' });
    fechar.onclick = fecharCalculadora;
    const teclas = el('div', { className: 'calc-teclas' }, ...TECLAS_CALC.map(([t, classe]) => {
      const b = el('button', { type: 'button', className: classe || '', textContent: t });
      b.setAttribute('aria-label', { C: 'Limpar', '⌫': 'Apagar', '×': 'Multiplicar', '÷': 'Dividir', '=': 'Igual', ',': 'Vírgula', '%': 'Porcentagem' }[t] || t);
      b.onclick = () => digitarCalc(t);
      return b;
    }));
    const visor = el('div', { className: 'calc-visor', 'aria-live': 'polite' }, el('div', { id: 'calc-ultima', className: 'calc-ultima', textContent: c.ultima }),
      el('div', { id: 'calc-expr', className: 'calc-expr', textContent: c.expr || '0' }),
      el('div', { id: 'calc-res', className: `calc-res${erro ? ' erro' : ''}`, textContent: erro ? 'Conta incompleta' : previa }));
    if (modoCalc) {
      // Janela própria: a barra de título do Windows já diz "Calculadora" e tem o X; aqui ficam só o visor e as teclas.
      folha.replaceChildren(visor, teclas);
      return;
    }
    folha.setAttribute('aria-labelledby', 'calc-titulo');
    folha.replaceChildren(el('h2', { id: 'calc-titulo', textContent: 'Calculadora' }), visor,
      teclas, el('p', { className: 'calc-dica', textContent: 'Dá para digitar pelo teclado: números, + - * /, Enter para o resultado, Backspace para apagar e Esc para fechar.' }),
      el('div', { className: 'acoes' }, fechar));
    fechar.focus();
  }
  // Dentro do Terminal 2.11+, a calculadora abre numa janela própria do Windows (solta, redimensionável, visível em qualquer
  // aba), pedido do dono em 21/11/2026. Nos Terminais antigos, no celular e no navegador continua a folha sobre a tela inicial.
  function abrirCalculadora() {
    if (P.hosted && P.calc && ponte) { aoPrograma({ type: 'calc' }); return; }
    if (S.calcAberto) return; S.appsAberto = false; S.calcAberto = true; desenhar();
  }
  function fecharCalculadora() {
    if (modoCalc) { aoPrograma({ type: 'calc-close' }); return; }
    S.calcAberto = false; desenhar();
  }
  if (modoCalc) {
    // Só a calculadora, ocupando a janela toda; o resto da tela inicial (e as ligações com o servidor) não roda.
    document.title = 'Calculadora';
    $('app-at').hidden = true;
    document.body.classList.add('modo-calc');
    const janela = el('div', { id: 'calc-janela', className: 'calc-janela' });
    janela.setAttribute('role', 'application');
    janela.setAttribute('aria-label', 'Calculadora');
    document.body.append(janela);
    desenharCalculadora(janela, true);
    document.addEventListener('keydown', teclaCalculadora);
    return;
  }

  // ================================================================ ligações
  $('busca').oninput = desenhar;
  $('at-chamar').onclick = chamarTi;
  // Modo bloqueado: sem menu Iniciar, a atendente abre os arquivos e desliga o computador por aqui.
  if (P.locked) {
    $('at-arquivos').hidden = false;
    $('at-energia').hidden = false;
    $('at-arquivos').onclick = () => aoPrograma({ type: 'files' });
    $('at-energia').onclick = () => aoPrograma({ type: 'power' });
  }
  // Aplicativos do computador: só dentro de um programa Terminal que sabe listá-los (apps: true; os antigos não mostram o botão).
  if (P.hosted && !P.local && P.apps) {
    $('at-aplicativos').hidden = false;
    $('at-aplicativos').onclick = abrirAplicativos;
  }
  $('at-ver-aviso').onclick = () => { S.avisoAberto = true; desenhar(); };
  $('at-calc').onclick = abrirCalculadora;
  // Ferramentas de PDF: janela própria do Terminal com o Pontual Tools local (MainWindow.PdfTools.cs).
  if (P.hosted && P.pdf && ponte) { $('at-pdf').hidden = false; $('at-pdf').onclick = () => aoPrograma({ type: 'pdf' }); }
  // Notas adesivas da Pontual (pedido do dono, 25/11/2026): cada nota é uma janela própria do Terminal (MainWindow.Notes.cs).
  // As janelas só gravam no armazenamento local; esta tela, que fica aberta o dia todo, é quem envia as notas para a
  // Central e busca os recados do TI (notas-base.js). No navegador comum, para teste, a lista abre em outra aba.
  const NOTAS = window.PontualNotas || null;
  const temNotas = Boolean(NOTAS) && (P.hosted ? P.notes && Boolean(ponte) : Boolean(P.token));
  const mostrarNotas = (notas) => { for (const n of notas) aoPrograma({ type: 'note-show', id: n.id, color: n.color, x: n.x, y: n.y, w: n.w, h: n.h, activate: false }); };
  let notasTimer = 0;
  async function sincronizarNotas() {
    if (!temNotas || P.local || S.invalido || (S.estado !== 'active' && S.estado !== 'maintenance')) return;
    try {
      const r = await NOTAS.sincronizar(api);
      if (!r) return;
      // Recado novo do TI aparece na tela; recado retirado pelo TI some.
      mostrarNotas(r.mostrar);
      for (const id of r.fechar) aoPrograma({ type: 'note-close', id });
    } catch (e) {
      if (e.status === 401) { S.invalido = true; desenhar(); }
      // Sem conexão ou máquina pendente: as notas continuam guardadas aqui e a próxima rodada tenta de novo.
    }
  }
  if (temNotas) {
    $('at-notas').hidden = false;
    $('at-notas').onclick = () => { if (ponte) aoPrograma({ type: 'notes' }); else window.open('notas.html', 'notas-lista', 'width=360,height=600'); };
    // Uma janela de nota gravou alguma coisa: manda para a Central logo em seguida.
    window.addEventListener('storage', (e) => { if (NOTAS.ehChave(e.key)) { clearTimeout(notasTimer); notasTimer = setTimeout(sincronizarNotas, 1500); } });
    setInterval(sincronizarNotas, 20000);
    setTimeout(sincronizarNotas, 3000);
    // Ao ligar o Terminal, as notas que estavam abertas voltam para o mesmo lugar da tela.
    mostrarNotas(NOTAS.todas().filter((n) => n.open));
  }
  $('at-veu').onclick = (e) => { if (e.target === $('at-veu')) { if (S.appsAberto) fecharAplicativos(); else if (S.calcAberto) fecharCalculadora(); else fecharAviso(); } };
  document.addEventListener('keydown', (e) => {
    if (S.calcAberto) { teclaCalculadora(e); return; }
    if (S.appsAberto) { if (e.key === 'Escape') fecharAplicativos(); return; }
    if (!S.avisoAberto) return;
    if (e.key === 'Escape') fecharAviso();
    // A folha só tem um botão: o Tab fica nele.
    if (e.key === 'Tab') { e.preventDefault(); const ok = $('at-folha').querySelector('button'); if (ok) ok.focus(); }
  });

  for (const alvo of document.querySelectorAll('[data-logo]')) {
    const img = el('img', { src: 'https://www.pontualconsig.com.br/frontend/assets/img/LOGO%20PONTUAL%20CONSIG%203.svg', alt: 'Pontual Consig' });
    // Sem internet, a logo é trocada pelo nome escrito.
    img.onerror = () => alvo.replaceChildren(el('span', { className: 'logo-texto' }, 'Pontual', el('em', { textContent: 'Consig' })));
    alvo.append(img);
  }

  // Mensagens do programa: "state" (abas abertas) é do contrato; "config" avisa que a configuração mudou;
  // "status" traz o estado da máquina, que só o programa conhece, porque é ele quem envia o heartbeat.
  if (ponte) {
    ponte.addEventListener('message', (e) => {
      const m = e.data;
      if (!m || typeof m !== 'object') return;
      if (m.type === 'state' && Array.isArray(m.open)) {
        S.abertos = m.open.filter((a) => a && typeof a.id === 'string' && typeof a.name === 'string').map((a) => ({ id: a.id, name: texto(a.name, 120) }));
        desenhar();
      } else if (m.type === 'config') {
        // No modo local a configuração vem na própria mensagem (o arquivo foi editado).
        if (P.local) { if (m.config) usarConfigLocal(m.config); desenhar(); } else carregarConfig();
      } else if (m.type === 'apps' && Array.isArray(m.apps)) {
        S.apps = m.apps.filter((a) => a && typeof a.id === 'string' && typeof a.name === 'string').slice(0, 500).map((a) => ({
          id: a.id, name: texto(a.name, 120), exe: texto(a.exe, 120).toLowerCase(),
          source: ['windows', 'store', 'installed', 'downloaded'].includes(a.source) ? a.source : 'installed',
          status: ['blocked', 'allowed', 'request'].includes(a.status) ? a.status : 'request', open: a.open === true,
        }));
        desenhar();
      } else if (m.type === 'status') {
        const voltou = S.online === false && m.online === true;
        if (typeof m.online === 'boolean') S.online = m.online;
        aplicarEstado(m);
        if (S.estado !== 'pending' && (!S.config || voltou)) carregarConfig();
        desenhar();
      }
    });
  }

  function usarConfigLocal(c) {
    if (!c || typeof c !== 'object') return;
    S.config = lerConfig(c);
    S.nome = S.config.attendantName;
    S.estado = 'active';
  }

  // ================================================================== início
  if (P.local) usarConfigLocal(injetado.config);
  relogio();
  setInterval(() => { relogio(); desenharSaudacao(); }, 20000);
  desenhar();
  if (P.hosted) {
    aoPrograma({ type: 'ready' });   // o programa responde com "state" e "status"
    if (!P.local) carregarConfig();
  } else if (!P.token) {
    S.invalido = true;
    desenhar();
  } else {
    evento('session_started');
    sinal();
    setInterval(sinal, INTERVALO_SINAL);
    setInterval(vigiarJanelas, 1500);
  }
})();
