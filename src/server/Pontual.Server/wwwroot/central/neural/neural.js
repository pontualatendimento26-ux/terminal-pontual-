// Central Neural da IA — telas de gestão do conhecimento da assistente.
// JavaScript puro, sem etapa de build. Fala com /api/v1/neural (só administrador e superadministrador).
// Regras do arquivo: nada que vem do servidor entra na página como HTML (sempre textContent, via el());
// todo número mostrado sai dos dados reais; onde não há dado, a tela diz que está vazia.
'use strict';

// ================================================================= ícones
// Textos fixos deste arquivo; é o único uso de innerHTML e nunca recebe dado do servidor.
const IC = {
  cerebro: '<circle cx="12" cy="12" r="3"/><circle cx="12" cy="12" r="9" stroke-dasharray="3 3"/><path d="M12 3v3M12 18v3M3 12h3M18 12h3"/>',
  roteiro: '<path d="M6 3h9l4 4v14H6z"/><path d="M14 3v5h5M9 13h7M9 17h5"/>',
  documento: '<path d="M4 6a2 2 0 0 1 2-2h4l2 2h6a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"/>',
  conhecimento: '<path d="M12 3a6 6 0 0 0-3.5 10.9V16h7v-2.1A6 6 0 0 0 12 3Z"/><path d="M9.5 19h5M10.5 21.5h3"/>',
  aprender: '<path d="M3 9l9-5 9 5-9 5-9-5Z"/><path d="M7 11.5V16c1.5 1.5 3.2 2 5 2s3.5-.5 5-2v-4.5"/>',
  base: '<ellipse cx="12" cy="6" rx="7" ry="3"/><path d="M5 6v6c0 1.7 3.1 3 7 3s7-1.3 7-3V6M5 12v6c0 1.7 3.1 3 7 3s7-1.3 7-3v-6"/>',
  visao: '<path d="M4 20V10M10 20V4M16 20v-8M22 20H2"/>',
  voltar: '<path d="M10 6l-6 6 6 6M4 12h16"/>',
  lupa: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>', x: '<path d="M6 6l12 12M18 6 6 18"/>',
  mais: '<path d="M12 5v14M5 12h14"/>', menos: '<path d="M5 12h14"/>',
  enquadrar: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
  expandir: '<path d="M7 9l5-5 5 5M7 15l5 5 5-5"/>',
  recolher: '<path d="M7 4l5 5 5-5M7 20l5-5 5 5"/>',
  editar: '<path d="M4 20h4L19 9l-4-4L4 16v4Z"/><path d="m13.5 6.5 4 4"/>',
  lixo: '<path d="M4 7h16M10 11v6M14 11v6"/><path d="M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12"/><path d="M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>',
  elo: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  baixar: '<path d="M12 4v11M7 11l5 5 5-5M5 20h14"/>', enviar: '<path d="M12 16V5M7 9l5-5 5 5M5 20h14"/>',
  mira: '<circle cx="12" cy="12" r="7"/><path d="M12 2v4M12 18v4M2 12h4M18 12h4"/>',
  relogio: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  raio: '<path d="M13 3 5 13h6l-1 8 8-10h-6l1-8Z"/>', abrir: '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/>',
};
function ic(nome, tam = 18) {
  const s = document.createElement('span');
  s.innerHTML = `<svg width="${tam}" height="${tam}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${IC[nome]}</svg>`;
  return s.firstChild;
}
for (const alvo of document.querySelectorAll('[data-ic]')) alvo.replaceWith(ic(alvo.dataset.ic, Number(alvo.dataset.tam) || 18));

// ============================================================== utilidades
const $ = (id) => document.getElementById(id);
function el(tag, props = {}, ...filhos) {
  const n = document.createElement(tag);
  for (const [chave, valor] of Object.entries(props)) {
    if (valor === null || valor === undefined) continue;
    if (chave === 'style') n.style.cssText = valor;
    else if (chave === 'role' || chave === 'for' || chave.startsWith('aria-') || chave.startsWith('data-')) n.setAttribute(chave, valor);
    else n[chave] = valor;
  }
  n.append(...filhos.filter((f) => f !== null && f !== undefined && f !== false));
  return n;
}
const texto = (v) => (v === null || v === undefined ? '' : String(v));
const mil = (n) => Number(n || 0).toLocaleString('pt-BR');
const plural = (n, um, varios) => `${mil(n)} ${n === 1 ? um : varios}`;
const semAcento = (s) => texto(s).normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
function quando(iso) {
  const d = iso ? new Date(iso) : null;
  if (!d || Number.isNaN(d.getTime())) return 'Sem data';
  const hoje = new Date(), hora = d.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
  if (d.toDateString() === hoje.toDateString()) return `Hoje, ${hora}`;
  if (d.toDateString() === new Date(hoje.getTime() - 864e5).toDateString()) return `Ontem, ${hora}`;
  return `${d.toLocaleDateString('pt-BR')}, ${hora}`;
}
const recente = (iso) => Boolean(iso) && Date.now() - new Date(iso).getTime() < 7 * 864e5;
function tamanho(bytes) { return bytes >= 1048576 ? `${(bytes / 1048576).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} MB` : `${Math.max(1, Math.round(bytes / 1024)).toLocaleString('pt-BR')} KB`; }
const botao = (rotulo, icone, classe = 'btn claro mini') => el('button', { type: 'button', className: classe }, icone ? ic(icone, 16) : null, rotulo);
const iconeBotao = (icone, rotulo, classe = '') => el('button', { type: 'button', className: `icone ${classe}`, title: rotulo, 'aria-label': rotulo }, ic(icone, 17));
const selo = (classe, rotulo) => el('span', { className: `selo ${classe}`, textContent: rotulo });
const vazio = (titulo, frase, extra) => el('div', { className: 'vazio' }, el('b', { textContent: titulo }), frase ? el('span', { textContent: frase }) : null, extra || null);

const TIPOS = {
  script: { um: 'Roteiro', varios: 'Roteiros', cor: MAPA_CORES.script },
  document: { um: 'Documento', varios: 'Documentos', cor: MAPA_CORES.document },
  knowledge: { um: 'Conhecimento', varios: 'Conhecimentos operacionais', cor: MAPA_CORES.knowledge },
  attendant: { um: 'Atendente', varios: 'Atendentes', cor: MAPA_CORES.attendant },
  suggestion: { um: 'Sugestão pendente', varios: 'Aprendizado', cor: MAPA_CORES.suggestion },
};
const SUGESTOES = { correction: 'Correção de atendente', gap: 'Lacuna de conhecimento', new_info: 'Informação nova', review: 'Conteúdo a revisar' };
const SITUACAO = { pending: 'Pendente', approved: 'Aprovada', rejected: 'Rejeitada' };
const ORIGENS = { manual: 'Cadastro manual', arquivo: 'Arquivo enviado', atendentes: 'Sugestão aprovada', sistema: 'Carga inicial' };
const CATEGORIAS = ['Procedimentos', 'Orientações', 'Regras', 'Instruções', 'Informações sobre bancos', 'Processos', 'Dúvidas frequentes', 'Conteúdos complementares'];
const ESTADOS = { active: 'Ativo', disabled: 'Desativado', pending: 'Aguardando leitor de PDF' };
const estadoDe = (i) => (i.enabled ? 'active' : i.fileStatus === 'no_text' ? 'pending' : 'disabled');
const VISTAS = {
  mapa: ['Cérebro Neural', 'Tudo o que alimenta a IA da Pontual e como se conecta.'],
  roteiros: ['Roteiros', 'Os roteiros operacionais que a IA segue para responder.'],
  documentos: ['Documentos', 'PDF, DOCX e TXT: o texto é extraído e entra na base.'],
  aprendizado: ['Aprendizado das atendentes', 'O que as conversas ensinam. Nada vira fonte oficial sem a sua aprovação.'],
  horarios: ['Horários da IA', 'Os dias e os horários em que a IA pode atender os clientes no lugar de cada atendente.'],
  base: ['Base de Conhecimento', 'Tudo o que a IA possui, em um só lugar.'],
  visao: ['Visão Geral', 'Os números reais do cérebro da IA.'],
};

// ================================================================== estado
const E = {
  eu: null, d: null, vista: 'mapa', mapa: null, gaveta: null,
  ocultos: new Set(), estadoMapa: '', listas: {}, sugestoes: null, situacao: 'pending',
  horarios: new Map(), // horários mexidos e ainda não salvos, por máquina
  itemAberto: null, ligando: null, ligarEscolha: null, sugestaoAberta: null, substituir: null, envios: [],
};
const item = (id) => (E.d ? E.d.items.find((i) => i.id === id) : null) || null;
const itensDe = (tipo) => (E.d ? E.d.items.filter((i) => i.kind === tipo) : []);

// ===================================================================== API
class ErroApi extends Error { constructor(status, mensagem) { super(mensagem); this.status = status; } }
async function api(metodo, caminho, corpo) {
  const opcoes = { method: metodo, credentials: 'same-origin', headers: { Accept: 'application/json' } };
  if (metodo !== 'GET') opcoes.headers['X-Pontual'] = '1';
  if (corpo !== undefined) { opcoes.headers['Content-Type'] = 'application/json'; opcoes.body = JSON.stringify(corpo); }
  let resposta;
  try { resposta = await fetch(`/api/v1${caminho}`, opcoes); } catch { throw new ErroApi(0, 'Sem conexão com o servidor. Tente de novo em instantes.'); }
  let dados = null;
  if (resposta.status !== 204) { const bruto = await resposta.text().catch(() => ''); if (bruto) { try { dados = JSON.parse(bruto); } catch { dados = null; } } }
  if (resposta.ok) return dados;
  if (resposta.status === 401) { location.href = '/central/'; }
  const padrao = { 403: 'A sua função não permite fazer isso.', 404: 'Não encontrado. O item pode ter sido removido.', 413: 'Os dados enviados são grandes demais.', 429: 'Muitas tentativas em pouco tempo. Aguarde um minuto.' };
  throw new ErroApi(resposta.status, (dados && typeof dados.error === 'string' && dados.error.trim()) || padrao[resposta.status] || `O servidor respondeu com o erro ${resposta.status}.`);
}

let relogioRecado = null;
function recado(mensagem, falhou = false) {
  clearTimeout(relogioRecado);
  $('recado-texto').textContent = mensagem;
  $('recado').classList.toggle('falha', falhou);
  $('recado').hidden = false;
  if (!falhou) relogioRecado = setTimeout(() => { $('recado').hidden = true; }, 7000);
}
$('recado-fechar').onclick = () => { $('recado').hidden = true; };

// Executa uma ação: trava o botão, mostra o resultado e recarrega a base.
async function agir(alvo, fazer, ok) {
  if (alvo) alvo.disabled = true;
  try {
    const resultado = await fazer();
    await carregar();
    if (ok) recado(typeof ok === 'function' ? ok(resultado) : ok);
    return resultado ?? true;
  } catch (e) {
    recado(e.message, true);
    return null;
  } finally { if (alvo && alvo.isConnected) alvo.disabled = false; }
}

// ================================================================= partida
async function iniciar() {
  let eu;
  try { eu = await api('GET', '/auth/me'); } catch (e) {
    if (e.status === 401) return;
    $('carga').hidden = true; $('negado').hidden = false;
    $('negado-titulo').textContent = 'Sem conexão'; $('negado-texto').textContent = e.message;
    return;
  }
  if (!['admin', 'superadmin'].includes(eu.role)) { $('carga').hidden = true; $('negado').hidden = false; return; }
  E.eu = eu;
  $('eu-nome').textContent = eu.name;
  $('eu-funcao').textContent = eu.role === 'superadmin' ? 'Superadministrador' : 'Administrador';
  try { E.d = await api('GET', '/neural'); } catch (e) {
    $('carga').hidden = true; $('negado').hidden = false;
    $('negado-titulo').textContent = 'Não foi possível abrir'; $('negado-texto').textContent = e.message;
    return;
  }
  $('carga').hidden = true; $('app').hidden = false;
  E.mapa = new MapaNeural($('mapa'), { aoSelecionar: aoSelecionarNo, aoPassar: dicaDoNo });
  ir(VISTAS[location.hash.slice(1)] ? location.hash.slice(1) : 'mapa');
}

async function carregar() {
  E.d = await api('GET', '/neural');
  E.sugestoes = null;
  desenhar();
}

function ir(vista) {
  E.vista = vista;
  for (const b of document.querySelectorAll('#nav button')) { if (b.dataset.ir === vista) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); }
  for (const s of document.querySelectorAll('.vista')) s.hidden = s.dataset.vista !== vista;
  [$('titulo').textContent, $('subtitulo').textContent] = VISTAS[vista];
  history.replaceState(null, '', `#${vista}`);
  if (vista !== 'mapa') fecharGaveta();
  desenhar();
  if (vista === 'mapa') E.mapa.redimensionar();
}
for (const b of document.querySelectorAll('#nav button')) b.onclick = () => ir(b.dataset.ir);

function desenhar() {
  const d = E.d;
  $('n-roteiros').textContent = mil(itensDe('script').length);
  $('n-documentos').textContent = mil(itensDe('document').length);
  $('n-base').textContent = mil(d.items.length);
  $('n-pendentes').textContent = mil(d.suggestions.pending); $('n-pendentes').hidden = !d.suggestions.pending;

  const chip = $('chip-modo');
  chip.classList.toggle('atencao', !d.settings.hasKey);
  if (!d.settings.hasKey) { chip.textContent = 'Sem chave da IA'; chip.title = 'Cadastre a chave em Central do TI → Assistente. Sem ela a assistente não responde nem analisa conversas.'; }
  else if (d.brain.mode === 'inline') { chip.textContent = 'Leitura completa'; chip.title = 'A base cabe inteira: a IA lê tudo a cada pergunta.'; }
  else { chip.textContent = 'Índice + consulta'; chip.title = 'A base cresceu: a IA lê o índice e abre só o que a pergunta pede.'; }

  E.mapa.setDados(grafo());
  desenharFiltrosMapa();
  if (E.vista === 'roteiros') desenharLista('roteiros');
  else if (E.vista === 'documentos') desenharLista('documentos');
  else if (E.vista === 'base') desenharLista('base');
  else if (E.vista === 'aprendizado') desenharAprendizado();
  else if (E.vista === 'horarios') desenharHorarios();
  else if (E.vista === 'visao') desenharVisao();
}

// ==================================================================== mapa
function grafo() {
  const d = E.d, nos = [{ id: 'ia', pai: null, tipo: 'nucleo', rotulo: 'IA Pontual' }];
  for (const tipo of ['script', 'document', 'knowledge']) {
    const itens = itensDe(tipo), ramo = `r:${tipo}`;
    nos.push({ id: ramo, pai: 'ia', tipo: 'ramo', ramo: tipo, rotulo: TIPOS[tipo].varios, n: itens.length });
    const bancos = new Map();
    for (const i of itens) { const b = i.bank || ''; if (!bancos.has(b)) bancos.set(b, []); bancos.get(b).push(i); }
    // Com muitos itens de bancos diferentes, cada banco vira um grupo que abre e fecha.
    const agrupar = itens.length > 12 && bancos.size > 1;
    if (agrupar) for (const [banco, lista] of [...bancos].sort((a, b) => a[0].localeCompare(b[0], 'pt-BR'))) {
      nos.push({ id: `g:${tipo}:${banco}`, pai: ramo, tipo: 'grupo', ramo: tipo, rotulo: banco || 'Sem banco definido', n: lista.length });
    }
    for (const i of itens) {
      nos.push({ id: i.id, pai: agrupar ? `g:${tipo}:${i.bank || ''}` : ramo, tipo, rotulo: i.title, sub: i.bank, estado: estadoDe(i), novo: recente(i.updatedAt) });
    }
  }
  nos.push({ id: 'r:attendant', pai: 'ia', tipo: 'ramo', ramo: 'attendant', rotulo: 'Atendentes', n: d.attendants.length });
  for (const a of d.attendants) {
    nos.push({ id: `a:${a.deviceId}`, pai: 'r:attendant', tipo: 'attendant', rotulo: a.name, estado: a.hasProfile ? 'active' : a.questions || a.whatsappPending ? 'pending' : 'disabled', novo: (a.newMessages > 0 || a.whatsappPending > 0) && a.hasProfile });
  }
  nos.push({ id: 'r:suggestion', pai: 'ia', tipo: 'ramo', ramo: 'suggestion', rotulo: 'Aprendizado', n: d.suggestions.pending });
  const agruparSug = d.pendingSuggestions.length > 10;
  if (agruparSug) for (const tipo of Object.keys(SUGESTOES)) {
    const n = d.pendingSuggestions.filter((s) => s.kind === tipo).length;
    if (n) nos.push({ id: `g:sug:${tipo}`, pai: 'r:suggestion', tipo: 'grupo', ramo: 'suggestion', rotulo: SUGESTOES[tipo], n });
  }
  for (const s of d.pendingSuggestions) nos.push({ id: `s:${s.id}`, pai: agruparSug ? `g:sug:${s.kind}` : 'r:suggestion', tipo: 'suggestion', rotulo: s.title, estado: 'pending' });
  return { nos, ligacoes: d.links.map((l) => ({ a: l.from, b: l.to })) };
}

function aplicarFiltroMapa() {
  E.mapa.setFiltro((no) => {
    if (E.ocultos.has(no.tipo)) return false;
    if (!E.estadoMapa) return true;
    if (E.estadoMapa === 'novo') return Boolean(no.novo);
    return no.estado === E.estadoMapa;
  });
}

function desenharFiltrosMapa() {
  const d = E.d, contagem = { script: itensDe('script').length, document: itensDe('document').length, knowledge: itensDe('knowledge').length, attendant: d.attendants.length, suggestion: d.suggestions.pending };
  const chips = Object.keys(contagem).map((tipo) => {
    const b = el('button', { type: 'button', className: 'filtro', style: `--c:${TIPOS[tipo].cor}`, 'aria-pressed': String(!E.ocultos.has(tipo)), title: `Mostrar ou esconder: ${TIPOS[tipo].varios}` },
      el('i'), tipo === 'suggestion' ? 'Sugestões pendentes' : TIPOS[tipo].varios, el('b', { textContent: mil(contagem[tipo]) }));
    b.onclick = () => { if (E.ocultos.has(tipo)) E.ocultos.delete(tipo); else E.ocultos.add(tipo); aplicarFiltroMapa(); desenharFiltrosMapa(); };
    return b;
  });
  const estado = el('select', { className: 'filtro', 'aria-label': 'Filtrar o mapa por situação' },
    ...[['', 'Todas as situações'], ['active', 'Ativos'], ['disabled', 'Desativados'], ['pending', 'Pendentes'], ['novo', 'Atualizados em 7 dias']]
      .map(([v, r]) => el('option', { value: v, textContent: r, selected: v === E.estadoMapa })));
  estado.onchange = () => { E.estadoMapa = estado.value; aplicarFiltroMapa(); };
  $('mapa-filtros').replaceChildren(...chips, estado);
  $('mapa-rodape').textContent = `${plural(d.items.length, 'conhecimento', 'conhecimentos')} · ${plural(d.links.length, 'ligação', 'ligações')} · ${plural(d.attendants.length, 'atendente', 'atendentes')}. Arraste para mover, role para aproximar, clique num ramo para abrir ou fechar.`;
}

$('z-mais').onclick = () => E.mapa.zoom(1.25);
$('z-menos').onclick = () => E.mapa.zoom(1 / 1.25);
$('z-tudo').onclick = () => E.mapa.ajustar();
$('z-abrir').onclick = () => E.mapa.expandirTudo();
$('z-fechar').onclick = () => E.mapa.recolherTudo();

function fecharMenu() { $('mapa-menu').hidden = true; $('mapa-novo').setAttribute('aria-expanded', 'false'); }
$('mapa-novo').onclick = (e) => { e.stopPropagation(); const abrir = $('mapa-menu').hidden; $('mapa-menu').hidden = !abrir; $('mapa-novo').setAttribute('aria-expanded', String(abrir)); };
for (const b of document.querySelectorAll('#mapa-menu [data-novo]')) b.onclick = () => { fecharMenu(); if (b.dataset.novo === 'document') escolherArquivo(null); else abrirFormulario(null, b.dataset.novo); };
document.addEventListener('click', (e) => { if (!e.target.closest('.menu-caixa')) fecharMenu(); if (!e.target.closest('#busca-caixa')) $('busca-lista').hidden = true; });

function dicaDoNo(no, x, y) {
  const dica = $('dica-no');
  if (!no) { dica.hidden = true; return; }
  const linhas = [];
  if (no.tipo === 'nucleo') linhas.push('O núcleo: a assistente da Pontual.', 'Clique para ver como ela lê a base.');
  else if (no.tipo === 'ramo' || no.tipo === 'grupo') linhas.push(`${plural(no.n ?? no.filhos.length, 'item', 'itens')}. Clique para ${E.mapa.recolhidos.has(no.id) ? 'abrir' : 'fechar'}.`);
  else if (no.tipo === 'attendant') linhas.push('Atendente', no.estado === 'active' ? 'Perfil montado pela IA' : no.estado === 'pending' ? 'Tem conversas ainda sem análise' : 'Ainda sem conversas para analisar');
  else if (no.tipo === 'suggestion') linhas.push('Sugestão pendente de aprovação');
  else linhas.push(`${TIPOS[no.tipo].um}${no.sub ? ` · ${no.sub}` : ''}`, `${ESTADOS[no.estado] || ''}${no.novo ? ' · atualizado nos últimos 7 dias' : ''}`);
  dica.replaceChildren(el('b', { textContent: no.rotulo }), ...linhas.filter(Boolean).map((l) => el('span', { textContent: l })));
  dica.hidden = false;
  const caixa = dica.getBoundingClientRect();
  dica.style.left = `${Math.min(x + 16, window.innerWidth - caixa.width - 12)}px`;
  dica.style.top = `${Math.min(y + 16, window.innerHeight - caixa.height - 12)}px`;
}

function aoSelecionarNo(no) {
  $('dica-no').hidden = true;
  if (!no) { fecharGaveta(); return; }
  if (no.tipo === 'nucleo') gavetaNucleo();
  else if (no.tipo === 'ramo' || no.tipo === 'grupo') gavetaRamo(no);
  else if (no.tipo === 'attendant') gavetaAtendente(no.id.slice(2));
  else if (no.tipo === 'suggestion') gavetaSugestao(no.id.slice(2));
  else gavetaItem(no.id);
}

// Vai ao mapa, leva a câmera até o nó e abre os detalhes dele.
function verNoMapa(id) {
  if (E.vista !== 'mapa') ir('mapa');
  if (!E.mapa.focar(id)) { E.ocultos.clear(); E.estadoMapa = ''; aplicarFiltroMapa(); desenharFiltrosMapa(); E.mapa.focar(id); }
  const no = E.mapa.no(id);
  if (no) aoSelecionarNo(no);
}

// ================================================================== gaveta
function gaveta(chave, ...partes) {
  E.gaveta = chave;
  $('gaveta-corpo').replaceChildren(...partes.filter(Boolean));
  $('gaveta').hidden = false;
  $('gaveta-corpo').scrollTop = 0;
}
function fecharGaveta() { E.gaveta = null; $('gaveta').hidden = true; if (E.mapa) E.mapa.selecionar(null); }
function topoGaveta(...selos) {
  const fechar = iconeBotao('x', 'Fechar os detalhes'); fechar.onclick = fecharGaveta;
  return el('div', { className: 'gaveta-topo' }, ...selos, fechar);
}
const dado = (rotulo, valor) => el('div', {}, el('dt', { textContent: rotulo }), el('dd', { textContent: valor || '—' }));
const secao = (titulo, ...filhos) => el('section', {}, el('h3', { textContent: titulo }), ...filhos);

function gavetaNucleo() {
  const d = E.d, cheio = Math.min(100, Math.round((d.brain.enabledChars / d.brain.inlineBudget) * 100));
  gaveta('ia',
    topoGaveta(selo('script liso', 'Núcleo')),
    el('h2', { textContent: 'IA Pontual' }),
    el('p', { className: 'frase', textContent: 'A assistente que responde às atendentes pelo Terminal. Tudo o que ela sabe está nos ramos em volta deste núcleo.' }),
    el('dl', { className: 'dados' },
      dado('Modelo', d.settings.model), dado('Chave da IA', d.settings.hasKey ? 'Cadastrada' : 'Não cadastrada'),
      dado('Conhecimentos ativos', mil(d.items.filter((i) => i.enabled).length)), dado('Texto ativo', `${mil(d.brain.enabledChars)} caracteres`)),
    secao('Como ela lê a base',
      el('p', { className: 'frase', textContent: d.brain.mode === 'inline'
        ? 'Leitura completa: a base ativa cabe inteira, então a IA lê tudo a cada pergunta. É o modo mais preciso.'
        : 'Índice + consulta: a base passou do que cabe inteiro. A IA lê o índice (título, resumo e palavras-chave) e abre só os conhecimentos que a pergunta pede.' }),
      el('div', { className: `medidor${cheio >= 100 ? ' cheio' : ''}`, style: `--p:${cheio}%` }),
      el('p', { className: 'nota', textContent: `${mil(d.brain.enabledChars)} de ${mil(d.brain.inlineBudget)} caracteres da leitura completa. Acima disso, a troca para o índice é automática.` })),
    d.settings.hasKey ? null : el('p', { className: 'aviso', textContent: 'Sem a chave da IA a assistente não responde. Cadastre em Central do TI → Assistente.' }));
}

function gavetaRamo(no) {
  const filhos = no.filhos.slice().sort((a, b) => a.rotulo.localeCompare(b.rotulo, 'pt-BR'));
  const lista = filhos.map((f) => {
    const b = el('button', { type: 'button' }, el('i', { className: 'ponto', style: `--c:${E.mapa.cor(f)}` }), el('span', { className: 'nome', textContent: f.rotulo }),
      f.filhos.length ? el('small', { className: 'nota', textContent: mil(f.filhos.length) }) : null);
    b.onclick = () => verNoMapa(f.id);
    return b;
  });
  const novo = { 'r:script': 'script', 'r:knowledge': 'knowledge', 'r:document': 'document' }[no.id];
  let acao = null;
  if (novo) { acao = botao(novo === 'document' ? 'Enviar documento' : `Novo ${TIPOS[novo].um.toLowerCase()}`, novo === 'document' ? 'enviar' : 'mais', 'btn mini'); acao.onclick = () => (novo === 'document' ? escolherArquivo(null) : abrirFormulario(null, novo)); }
  if (no.id === 'r:suggestion') { acao = botao('Abrir o aprendizado', 'aprender', 'btn mini'); acao.onclick = () => ir('aprendizado'); }
  const vazios = { 'r:script': 'Nenhum roteiro cadastrado.', 'r:document': 'Nenhum documento enviado ainda.', 'r:knowledge': 'Nenhum conhecimento operacional cadastrado ainda.', 'r:attendant': 'Nenhuma máquina aprovada ainda.', 'r:suggestion': 'Nenhuma sugestão pendente. Elas aparecem quando uma atendente corrige uma resposta ou quando a IA analisa as conversas.' };
  gaveta(no.id,
    topoGaveta(selo(`${no.ramo} liso`, no.tipo === 'ramo' ? 'Ramo' : 'Grupo')),
    el('h2', { textContent: no.rotulo }),
    el('p', { className: 'frase', textContent: plural(no.n ?? filhos.length, 'item', 'itens') }),
    acao ? el('div', { className: 'botoes' }, acao) : null,
    lista.length ? el('div', { className: 'lista-simples' }, ...lista) : el('p', { className: 'nota', textContent: vazios[no.id] || 'Nada aqui com os filtros atuais.' }));
}

async function gavetaItem(id) {
  const resumo = item(id);
  if (!resumo) { fecharGaveta(); return; }
  gaveta(id, topoGaveta(selo(resumo.kind, TIPOS[resumo.kind].um)), el('h2', { textContent: resumo.title }), el('p', { className: 'nota', textContent: 'Carregando…' }));
  let c;
  try { c = await api('GET', `/neural/items/${id}`); } catch (e) { if (E.gaveta === id) gaveta(id, topoGaveta(), el('p', { className: 'erro', textContent: e.message })); return; }
  if (E.gaveta !== id) return;
  const i = c.item, estado = estadoDe(i);

  const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(i.enabled), 'aria-label': `${i.enabled ? 'Desativar' : 'Ativar'} ${i.title}`, disabled: !c.content });
  chave.onclick = () => agir(chave, () => api('PUT', `/neural/items/${id}`, { enabled: !i.enabled }), `${i.title}: ${i.enabled ? 'desativado, a IA deixou de usar' : 'ativado, a IA já usa'}.`).then(() => E.gaveta === id && gavetaItem(id));

  const editar = botao('Editar', 'editar', 'btn mini'); editar.onclick = () => abrirFormulario(c);
  const ligar = botao('Vincular', 'elo'); ligar.onclick = () => abrirLigar(i);
  const abrir = botao('Abrir o texto', 'abrir'); abrir.disabled = !c.content;
  abrir.onclick = () => { $('texto-titulo').textContent = i.title; $('texto-sub').textContent = `${mil(c.content.length)} caracteres${c.parts > 1 ? `, lidos pela IA em ${c.parts} partes no modo índice` : ''}.`; $('texto-corpo').textContent = c.content; $('dlg-texto').showModal(); };
  const excluir = botao('Excluir', 'lixo'); excluir.onclick = () => excluirItem(i);
  const botoes = [editar, abrir, ligar];
  if (i.kind === 'document') {
    const trocar = botao('Atualizar arquivo', 'enviar'); trocar.onclick = () => escolherArquivo(id);
    botoes.push(trocar);
    if (i.fileName) botoes.push(el('a', { className: 'btn claro mini', href: `/api/v1/neural/items/${id}/file`, download: i.fileName }, ic('baixar', 16), 'Baixar original'));
  }
  botoes.push(excluir);

  const porTipo = (tipo) => c.links.filter((l) => l.kind === tipo).map((l) => {
    const ir = el('button', { type: 'button', className: 'nome', style: 'background:none;border:none;color:inherit;text-align:left;padding:0', textContent: l.title }); ir.onclick = () => verNoMapa(l.id);
    const soltar = iconeBotao('x', `Desvincular de ${l.title}`); soltar.onclick = () => agir(soltar, () => api('DELETE', `/neural/links/${l.linkId}`), 'Ligação desfeita.').then(() => E.gaveta === id && gavetaItem(id));
    return el('div', {}, el('i', { className: 'ponto', style: `--c:${TIPOS[l.kind].cor}` }), ir, soltar);
  });
  const vinculos = ['script', 'document', 'knowledge'].map((tipo) => { const linhas = porTipo(tipo); return linhas.length ? secao(`${TIPOS[tipo].varios} vinculados`, el('div', { className: 'lista-simples' }, ...linhas)) : null; });

  gaveta(id,
    topoGaveta(selo(i.kind, TIPOS[i.kind].um), selo(estado, ESTADOS[estado]), recente(i.updatedAt) ? selo('liso', 'Atualizado há pouco') : null),
    el('h2', { textContent: i.title }),
    i.description ? el('p', { className: 'frase', textContent: i.description }) : null,
    el('div', { className: 'linha-opcao' }, el('div', {}, el('b', { textContent: i.enabled ? 'Em uso pela IA' : 'Fora de uso' }), el('small', { textContent: c.content ? 'Desligue para guardar sem a IA usar.' : 'Sem texto, não há o que a IA ler.' })), chave),
    i.fileStatus === 'no_text' ? el('p', { className: 'aviso', textContent: i.fileError }) : null,
    el('dl', { className: 'dados' },
      dado('Banco ou assunto', i.bank), dado('Categoria', i.category),
      dado('Origem', ORIGENS[i.source] || i.source), dado('Tamanho', `${mil(i.chars)} caracteres`),
      dado('Criado em', quando(i.createdAt)), dado('Atualizado', `${quando(i.updatedAt)}${i.updatedBy ? `, por ${i.updatedBy}` : ''}`),
      i.fileName ? dado('Arquivo', `${i.fileName} (${tamanho(i.fileSize)})`) : null,
      i.fileName ? dado('Processamento', { processed: 'Texto extraído', no_text: 'Sem texto: aguardando leitor', error: 'Erro na leitura' }[i.fileStatus] || '—') : null),
    el('div', { className: 'botoes' }, ...botoes),
    i.keywords ? secao('Palavras-chave', el('div', { className: 'marcas' }, ...i.keywords.split(',').map((p) => p.trim()).filter(Boolean).map((p) => el('span', { textContent: p })))) : null,
    c.aiInstructions ? secao('Instruções para a IA', el('p', { className: 'frase', textContent: c.aiInstructions })) : null,
    c.content ? secao('Conteúdo', el('div', { className: `trecho${c.content.length > 900 ? ' cortado' : ''}`, textContent: c.content.slice(0, 900) })) : null,
    ...vinculos,
    c.links.length ? null : secao('Vínculos', el('p', { className: 'nota', textContent: 'Nenhum vínculo. Use "Vincular" para ligar este conteúdo a um roteiro, documento ou conhecimento relacionado.' })),
    c.notes ? secao('Observações internas', el('p', { className: 'frase', textContent: c.notes })) : null);
}

async function excluirItem(i) {
  const sim = await confirmar({ titulo: `Excluir "${i.title}"?`, texto: `A IA deixa de conhecer este ${TIPOS[i.kind].um.toLowerCase()} e os vínculos dele somem${i.kind === 'document' ? ', junto com o arquivo guardado' : ''}. Se for só por um tempo, prefira desativar.`, botao: 'Excluir' });
  if (!sim) return;
  if (await agir(null, () => api('DELETE', `/neural/items/${i.id}`), `"${i.title}" foi excluído.`)) { if (E.gaveta === i.id) fecharGaveta(); }
}

async function gavetaAtendente(deviceId) {
  const chave = `a:${deviceId}`, resumo = E.d.attendants.find((a) => a.deviceId === deviceId);
  if (!resumo) { fecharGaveta(); return; }
  gaveta(chave, topoGaveta(selo('attendant', 'Atendente')), el('h2', { textContent: resumo.name }), el('p', { className: 'nota', textContent: 'Carregando…' }));
  let a;
  try { a = await api('GET', `/neural/attendants/${deviceId}`); } catch (e) { if (E.gaveta === chave) gaveta(chave, topoGaveta(), el('h2', { textContent: resumo.name }), el('p', { className: 'erro', textContent: e.message })); return; }
  if (E.gaveta !== chave) return;
  const p = a.profile;
  const analisar = botao(p ? 'Atualizar o perfil agora' : 'Montar o perfil agora', 'raio', 'btn mini');
  analisar.disabled = !(a.newMessages || a.whatsappPending) || !E.d.settings.hasKey;
  analisar.onclick = () => analisarAtendente(deviceId, a.name, analisar);
  const lista = (itens) => (itens && itens.length ? el('ul', { className: 'pontos' }, ...itens.map((t) => el('li', { textContent: t }))) : el('p', { className: 'nota', textContent: 'Nada registrado.' }));
  const maior = p && p.bancos && p.bancos.length ? Math.max(...p.bancos.map((b) => b.perguntas || 0), 1) : 1;
  const experiencia = { iniciante: 'Iniciante', intermediaria: 'Intermediária', experiente: 'Experiente', indefinida: 'Ainda indefinida' };

  gaveta(chave,
    topoGaveta(selo('attendant', 'Atendente'), p ? selo('liso', `Experiência: ${experiencia[p.experiencia] || 'indefinida'}`) : null),
    el('h2', { textContent: a.name }),
    el('dl', { className: 'dados' },
      dado('Computador', a.hostname), dado('Perguntas feitas', mil(a.questions)),
      dado('Falas analisadas', mil(a.messagesAnalyzed)), dado('Falas novas', mil(a.newMessages)),
      dado('Conversas de WhatsApp analisadas', mil(a.whatsappAnalyzed)), dado('Conversas de WhatsApp na fila', mil(a.whatsappPending)),
      dado('Respostas que ajudaram', mil(a.feedback.helpful)), dado('Respostas corrigidas', mil(a.feedback.notHelpful))),
    el('div', { className: 'botoes' }, analisar),
    !E.d.settings.hasKey ? el('p', { className: 'aviso', textContent: 'Sem a chave da IA não há análise.' }) : !a.newMessages && !a.whatsappPending && !p ? el('p', { className: 'nota', textContent: 'Ainda não há conversas dela para analisar.' }) : null,
    secaoCobertura(deviceId, a),
    a.history ? secaoHistorico(a.history, lista) : null,
    p ? secao('Resumo', el('p', { className: 'frase', textContent: p.resumo })) : a.history ? null : vazio('Perfil ainda não montado', 'A IA monta o perfil lendo as conversas dela com os clientes no WhatsApp e as perguntas que ela faz à assistente.'),
    p && p.atendimento ? secao('Como atende os clientes', el('p', { className: 'frase', textContent: p.atendimento })) : null,
    p ? secao('Como pergunta à assistente', el('p', { className: 'frase', textContent: p.estilo })) : null,
    p && p.bancos && p.bancos.length ? secao('Bancos mais consultados', el('div', { className: 'barras' }, ...p.bancos.slice().sort((x, y) => y.perguntas - x.perguntas).slice(0, 8).map((b) => el('div', {}, el('span', { textContent: b.nome }), el('b', { textContent: mil(b.perguntas) }), el('i', { style: `--p:${Math.round((b.perguntas / maior) * 100)}%` }))))) : null,
    p && p.temas && p.temas.length ? secao('Assuntos frequentes', el('div', { className: 'marcas' }, ...p.temas.map((t) => el('span', { textContent: t })))) : null,
    p ? secao('Dificuldades recorrentes', lista(p.dificuldades)) : null,
    p ? secao('Pontos fortes', lista(p.pontos_fortes)) : null,
    p ? secao('Recomendações para a supervisão', lista(p.recomendacoes)) : null,
    a.instructions ? secao('Orientações escritas para ela', el('p', { className: 'frase', textContent: a.instructions })) : null,
    a.suggestions.length ? secao('Sugestões que vieram dela', el('div', { className: 'lista-simples' }, ...a.suggestions.map((s) => el('div', {}, el('i', { className: 'ponto', style: `--c:${MAPA_CORES.suggestion}` }), el('span', { className: 'nome', textContent: s.title }), selo(`${s.status} liso`, SITUACAO[s.status]))))) : null,
    p ? el('p', { className: 'nota', textContent: `Perfil montado pela IA em ${quando(a.analyzedAt)}, a partir de ${plural(a.whatsappAnalyzed, 'conversa de WhatsApp', 'conversas de WhatsApp')} e de ${plural(a.messagesAnalyzed, 'fala', 'falas')} com a assistente. As conversas do WhatsApp são apagadas do servidor depois de analisadas. Esta leitura ficou registrada na auditoria.` }) : null);
}

// ============================================================= cobertura de ausência
// Quando a atendente falta, a assistente responde os clientes dela no WhatsApp, como ela. O simples vai
// sozinho; o que envolve dinheiro fica aqui esperando aprovação. "Ensaio" escreve as respostas sem enviar.
// Como a IA age no dia em que a atendente faltou. Fora da falta marcada ela nunca responde por ninguém.
const MODOS_COBERTURA = { on: 'Responde e envia', test: 'Ensaio (só escreve)' };
const SITUACAO_COBERTURA = {
  draft: ['Ensaio (não enviada)', 'liso'], pending: ['Esperando aprovação', 'pending'], approved: ['Aprovada, enviando', 'approved'],
  sending: ['Enviando', 'approved'], sent: ['Enviada', 'active'], rejected: ['Recusada', 'rejected'], failed: ['Não saiu', 'error'], skipped: ['Sem resposta', 'disabled'],
};
const DECISAO_COBERTURA = { send: 'a IA enviaria sozinha', queue: 'pede aprovação', none: 'não precisava responder' };

// Os três botões Desligada / Ensaio / Ligada. Usados na ficha da atendente e na aba Horários da IA.
function segmentoCobertura(deviceId, nome, atual, depois) {
  const efetivo = atual === 'test' ? 'test' : 'on';
  return el('div', { className: 'segmento', role: 'group', 'aria-label': `No dia em que ${nome} faltar, a IA` }, ...Object.entries(MODOS_COBERTURA).map(([modo, rotulo]) => {
    const b = el('button', { type: 'button', textContent: rotulo, 'aria-pressed': String(efetivo === modo) });
    b.onclick = async () => {
      if (modo === efetivo) return;
      if (await agir(b, () => api('PUT', `/neural/attendants/${deviceId}/coverage`, { mode: modo }), `Quando ${nome} faltar, a IA ${rotulo.toLowerCase()}.`) && depois) depois();
    };
    return b;
  }));
}

// "Faltou hoje": a IA passa a responder pela atendente na hora, só no dia marcado.
function botaoFalta(deviceId, nome, ausente, depois) {
  const b = botao(ausente ? 'Desmarcar a falta' : `${nome} faltou hoje`, null, ausente ? 'btn claro' : 'btn');
  b.title = ausente ? `A IA para de responder por ${nome}.` : `A IA responde os clientes de ${nome} no WhatsApp, hoje.`;
  b.onclick = async () => {
    if (!ausente && !confirm(`Marcar que ${nome} faltou hoje?\n\nA IA passa a abrir as conversas não lidas do WhatsApp de ${nome} e responder na hora, como ${nome}. O que envolver valor, prazo ou proposta espera a sua aprovação na ficha dela.\n\nVale só por hoje, nos horários da aba Horários da IA. O computador de ${nome} precisa estar ligado, com o Terminal e o WhatsApp abertos nele.`)) return;
    if (await agir(b, () => api('PUT', `/neural/attendants/${deviceId}/absence`, { absent: !ausente }),
      ausente ? `Falta de ${nome} desmarcada.` : `${nome} marcada como ausente hoje. A IA responde por ela.`) && depois) depois();
  };
  return b;
}

function secaoCobertura(deviceId, a) {
  const c = a.coverage || { mode: '', replies: [] };
  const segmento = segmentoCobertura(deviceId, a.name, c.mode, () => gavetaAtendente(deviceId));
  const falta = botaoFalta(deviceId, a.name, c.absent, () => gavetaAtendente(deviceId));
  const agenda = botao('Dias e horários', 'relogio'); agenda.onclick = () => ir('horarios');
  const explica = c.mode === 'test'
    ? `Ensaio: no dia da falta a assistente lê as mensagens novas e escreve a resposta, mas nada vai para o cliente. Use para conferir antes de deixar enviar.`
    : `No dia da falta a assistente responde sozinha, como ${a.name}, o que é simples. O que envolve dinheiro espera aprovação abaixo, com um aviso curto ao cliente na hora.`;
  const pendentes = c.replies.filter((r) => r.status === 'pending' || r.status === 'failed');
  const outras = c.replies.filter((r) => !pendentes.includes(r)).slice(0, 25);
  return secao('Cobertura de ausência',
    el('div', { className: 'botoes' }, falta),
    el('p', { className: 'nota', textContent: c.absent
      ? `${a.name} faltou hoje: a IA está respondendo os clientes dela na hora, como se fosse ela. Amanhã volta sozinha ao normal.`
      : `A IA só responde pelos clientes de ${a.name} no dia em que você marcar a falta. Se ${a.name} veio trabalhar e não está respondendo o WhatsApp, a IA não entra.` }),
    el('h4', { textContent: `No dia em que ${a.name} faltar, a IA` }),
    el('div', { className: 'botoes' }, segmento, agenda),
    el('p', { className: 'nota', textContent: explica }),
    c.absent && !c.withinHours ? el('p', { className: 'nota aviso', textContent: `Fora do horário agora: a IA só atende por ${a.name} em ${c.hours}. Se precisar que ela responda agora, mude o horário na aba Horários da IA.` }) : null,
    c.absent && c.withinHours && !c.status ? el('p', { className: 'nota', textContent: 'O Terminal dela ainda não contou o que está fazendo. Em até um minuto aparece aqui.' }) : null,
    c.absent && c.status ? el('p', { className: 'nota', textContent: `Agora no computador: ${c.status}${c.statusAt ? ` (${quando(c.statusAt)})` : ''}` }) : null,
    pendentes.length ? el('h4', { textContent: `Esperando aprovação (${pendentes.length})` }) : null,
    ...pendentes.map((r) => cartaoCobertura(deviceId, r, true)),
    outras.length ? el('h4', { textContent: 'Últimas respostas' }) : null,
    ...outras.map((r) => cartaoCobertura(deviceId, r, false)),
    !c.replies.length && c.mode ? el('p', { className: 'nota', textContent: 'Nenhuma mensagem tratada ainda. As conversas aparecem aqui assim que chegar mensagem nova de cliente no WhatsApp desse computador.' }) : null);
}

function cartaoCobertura(deviceId, r, editavel) {
  const [rotulo, classe] = SITUACAO_COBERTURA[r.status] || [r.status, 'liso'];
  const partes = [
    el('header', {}, selo(classe, rotulo), el('b', { textContent: r.contact }), el('small', { textContent: quando(r.sentAt || r.createdAt) })),
    el('div', { className: 'citacao' }, el('b', { textContent: 'Cliente escreveu' }), r.incoming),
  ];
  if (r.holding) partes.push(el('div', { className: 'citacao' }, el('b', { textContent: 'Aviso enviado na hora' }), r.holding));
  if (editavel) {
    const campo = el('textarea', { className: 'campo', value: r.reply, 'aria-label': `Resposta para ${r.contact}` });
    const aprovar = botao('Aprovar e enviar', null, 'btn mini');
    aprovar.onclick = async () => { if (await agir(aprovar, () => api('POST', `/neural/coverage/${r.id}/approve`, { message: campo.value }), 'Aprovada. O Terminal envia na próxima volta.')) gavetaAtendente(deviceId); };
    const recusar = botao('Recusar', null);
    recusar.onclick = async () => { if (await agir(recusar, () => api('POST', `/neural/coverage/${r.id}/reject`), 'Resposta recusada. Nada foi enviado.')) gavetaAtendente(deviceId); };
    partes.push(el('b', { textContent: 'Resposta proposta (você pode editar)' }), campo, el('div', { className: 'botoes' }, aprovar, recusar));
  } else if (r.reply) {
    partes.push(el('div', { className: 'citacao' }, el('b', { textContent: r.status === 'draft' ? `Resposta escrita (${DECISAO_COBERTURA[r.decision] || ''})` : 'Resposta' }), r.reply));
  }
  if (r.reason) partes.push(el('p', { className: 'nota', textContent: `Motivo: ${r.reason}${r.reviewedBy ? ` · aprovada por ${r.reviewedBy}` : ''}` }));
  if (r.error) partes.push(el('p', { className: 'erro', textContent: r.error }));
  partes.push(el('details', {}, el('summary', { textContent: 'Ver a conversa como estava' }), el('div', { className: 'citacao', textContent: r.transcript })));
  return el('div', { className: 'sugestao' }, ...partes);
}

// ================================================================ horários da IA
// Em que dias e horários a IA pode atender os clientes no lugar de cada atendente (a cobertura de ausência).
// O servidor manda sete dias por atendente, na posição do dia da semana (0 = domingo); a tela começa na segunda.
const DIAS = ['Domingo', 'Segunda', 'Terça', 'Quarta', 'Quinta', 'Sexta', 'Sábado'];
const ORDEM_DIAS = [1, 2, 3, 4, 5, 6, 0];
const mesmosDias = (a, b) => a.every((x, i) => x.on === b[i].on && (!x.on || (x.from === b[i].from && x.to === b[i].to)));
const corpoHorario = (dias) => ({ days: dias.map((x, day) => ({ day, on: x.on, from: x.from, to: x.to })) });

function desenharHorarios() {
  const cartoes = E.d.attendants.map(cartaoHorario);
  $('v-horarios').replaceChildren(
    el('div', { className: 'cartao' }, el('div', { className: 'cartao-topo' }, el('div', {},
      el('h2', { textContent: 'Quando a IA pode atender por cada atendente' }),
      el('p', { textContent: 'Marque os dias e escreva o horário de cada atendente. A IA só responde os clientes dela no WhatsApp no dia em que você marcar a falta dela na Central Neural, e dentro do horário marcado aqui; fora disso ela não abre nem responde nada. Os horários são de Brasília e a mudança chega ao Terminal em menos de um minuto.' })))),
    cartoes.length ? el('div', { className: 'horarios' }, ...cartoes) : vazio('Nenhuma atendente ainda', 'As máquinas aprovadas na Central do TI aparecem aqui.'));
}

function cartaoHorario(a) {
  const salvo = a.schedule, dias = E.horarios.get(a.deviceId) || salvo.map((x) => ({ ...x }));
  const salvar = botao('Salvar', null, 'btn mini'), desfazer = botao('Desfazer', null), copiar = botao('Usar em todas', null);
  const situacao = el('p', { className: 'nota' });
  const atualizar = () => {
    const mudou = !mesmosDias(dias, salvo);
    if (mudou) E.horarios.set(a.deviceId, dias); else E.horarios.delete(a.deviceId);
    salvar.disabled = desfazer.disabled = !mudou;
    situacao.textContent = mudou ? 'Há mudanças ainda não salvas neste cartão.'
      : !dias.some((x) => x.on) ? 'Nenhum dia marcado: a IA não atende por ela, mesmo no dia em que faltar.'
      : !a.absent ? 'Estes horários valem no dia em que você marcar que ela faltou.'
      : a.coverageNow ? (a.coverage === 'test' ? 'Faltou hoje e está dentro do horário: a IA está escrevendo as respostas em ensaio, sem enviar.' : 'Faltou hoje e está dentro do horário: a IA está atendendo por ela.')
      : 'Faltou hoje, mas agora está fora do horário: a IA não está atendendo por ela.';
  };
  const linhas = ORDEM_DIAS.map((n) => {
    const dia = dias[n];
    const marca = el('input', { type: 'checkbox', checked: dia.on });
    const de = el('input', { type: 'time', className: 'campo', value: dia.from, disabled: !dia.on, 'aria-label': `${DIAS[n]} de ${a.name}: início` });
    const ate = el('input', { type: 'time', className: 'campo', value: dia.to, disabled: !dia.on, 'aria-label': `${DIAS[n]} de ${a.name}: fim` });
    const linha = el('div', { className: `dia${dia.on ? '' : ' fora'}` }, el('label', {}, marca, DIAS[n]), de, el('span', { textContent: 'até' }), ate);
    marca.onchange = () => { dia.on = marca.checked; de.disabled = ate.disabled = !dia.on; linha.classList.toggle('fora', !dia.on); atualizar(); };
    de.onchange = () => { dia.from = de.value; atualizar(); };
    ate.onchange = () => { dia.to = ate.value; atualizar(); };
    return linha;
  });
  salvar.onclick = () => agir(salvar, () => api('PUT', `/neural/attendants/${a.deviceId}/coverage/schedule`, corpoHorario(dias)), `Horários de ${a.name} salvos.`);
  desfazer.onclick = () => { E.horarios.delete(a.deviceId); desenharHorarios(); };
  copiar.hidden = E.d.attendants.length < 2;
  copiar.onclick = async () => {
    if (!(await confirmar({ titulo: `Usar os horários de ${a.name} em todas?`, texto: `Os dias e horários deste cartão passam a valer para as ${E.d.attendants.length} atendentes, no lugar dos que cada uma tem hoje. Isso não liga nem desliga a cobertura de ninguém.`, botao: 'Usar em todas' }))) return;
    agir(copiar, async () => {
      for (const outra of E.d.attendants) await api('PUT', `/neural/attendants/${outra.deviceId}/coverage/schedule`, corpoHorario(dias));
      E.horarios.clear();
    }, 'Horários aplicados a todas as atendentes.');
  };
  atualizar();
  return el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' },
      el('div', {}, el('h2', { textContent: a.name }), el('p', { textContent: a.hostname })),
      segmentoCobertura(a.deviceId, a.name, a.coverage)),
    el('div', { className: 'cartao-corpo' }, el('div', { className: 'dias' }, ...linhas), situacao, el('div', { className: 'botoes' }, salvar, desfazer, copiar)));
}

// Perfil tirado do histórico de atendimentos com clientes (importado de uma vez, não pela análise diária).
function secaoHistorico(h, lista) {
  return secao('Do histórico de atendimentos com clientes',
    h.resumo ? el('p', { className: 'frase', textContent: h.resumo }) : null,
    h.atendimento ? el('p', { className: 'frase', textContent: h.atendimento }) : null,
    h.estilo ? el('p', { className: 'frase', textContent: h.estilo }) : null,
    h.pontos_fortes && h.pontos_fortes.length ? el('h4', { textContent: 'Pontos fortes' }) : null,
    h.pontos_fortes && h.pontos_fortes.length ? lista(h.pontos_fortes) : null,
    h.dificuldades && h.dificuldades.length ? el('h4', { textContent: 'Dificuldades' }) : null,
    h.dificuldades && h.dificuldades.length ? lista(h.dificuldades) : null,
    h.recomendacoes && h.recomendacoes.length ? el('h4', { textContent: 'Como a assistente pode ajudá-la' }) : null,
    h.recomendacoes && h.recomendacoes.length ? lista(h.recomendacoes) : null,
    el('p', { className: 'nota', textContent: 'Este trecho vai junto para a assistente desta atendente.' }));
}

async function analisarAtendente(deviceId, nome, alvo) {
  recado(`Analisando as conversas de ${nome}… pode levar até um minuto.`);
  const r = await agir(alvo, () => api('POST', `/neural/attendants/${deviceId}/analyze`),
    (x) => (x.messagesRead || x.conversationsRead ? `Perfil de ${nome} atualizado: ${plural(x.conversationsRead, 'conversa de WhatsApp lida', 'conversas de WhatsApp lidas')}, ${plural(x.messagesRead, 'fala com a assistente', 'falas com a assistente')}, ${plural(x.newSuggestions, 'sugestão nova', 'sugestões novas')}${x.more ? '. Ainda há material na fila; rode de novo para continuar' : ''}.` : `${nome} não tem nada novo para analisar.`));
  if (r && E.gaveta === `a:${deviceId}`) gavetaAtendente(deviceId);
}

async function buscarSugestoes() {
  if (!E.sugestoes) E.sugestoes = await api('GET', '/neural/suggestions');
  return E.sugestoes;
}
async function gavetaSugestao(id) {
  const chave = `s:${id}`;
  gaveta(chave, topoGaveta(selo('suggestion', 'Sugestão')), el('p', { className: 'nota', textContent: 'Carregando…' }));
  let s;
  try { s = (await buscarSugestoes()).find((x) => x.id === id); } catch (e) { gaveta(chave, topoGaveta(), el('p', { className: 'erro', textContent: e.message })); return; }
  if (E.gaveta !== chave) return;
  if (!s) { fecharGaveta(); return; }
  gaveta(chave, topoGaveta(selo('suggestion', SUGESTOES[s.kind] || 'Sugestão'), selo(`${s.status} liso`, SITUACAO[s.status])), ...corpoSugestao(s, true));
}

// ============================================================== formulário
function contar(campo, alvo, limite) { const n = campo.value.length; alvo.textContent = `${mil(n)} de ${mil(limite)} caracteres`; alvo.classList.toggle('erro', n > limite); }
$('item-texto').oninput = () => contar($('item-texto'), $('item-conta'), E.d.limits.item);

function opcoesDeLista() {
  const bancos = [...new Set(E.d.items.map((i) => i.bank).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'pt-BR'));
  const categorias = [...new Set([...CATEGORIAS, ...E.d.items.map((i) => i.category).filter(Boolean)])];
  $('lista-bancos').replaceChildren(...bancos.map((b) => el('option', { value: b })));
  $('lista-categorias').replaceChildren(...categorias.map((c) => el('option', { value: c })));
}

// completo = resposta de GET /neural/items/{id} (edição); nulo = novo do tipo indicado.
function abrirFormulario(completo, tipo = 'script') {
  E.itemAberto = completo;
  const i = completo ? completo.item : null, documento = i && i.kind === 'document';
  opcoesDeLista();
  $('item-titulo-dlg').textContent = i ? `Editar ${TIPOS[i.kind].um.toLowerCase()}` : `Novo ${TIPOS[tipo].um.toLowerCase()}`;
  $('item-explica').textContent = documento
    ? 'O texto abaixo foi extraído do arquivo. Você pode corrigir o que saiu errado; a IA lê exatamente o que está aqui.'
    : 'Depois de salvo e ativo, a IA passa a usar este conteúdo na pergunta seguinte.';
  $('item-tipo-caixa').hidden = Boolean(documento);
  $('item-tipo').value = i && !documento ? i.kind : tipo;
  $('item-status').value = i && !i.enabled ? '0' : '1';
  $('item-nome').value = i ? i.title : ''; $('item-descricao').value = i ? i.description : '';
  $('item-banco').value = i ? i.bank : ''; $('item-categoria').value = i ? i.category : ''; $('item-chaves').value = i ? i.keywords : '';
  $('item-texto').value = completo ? completo.content : ''; $('item-instrucoes').value = completo ? completo.aiInstructions : ''; $('item-obs').value = completo ? completo.notes : '';
  $('item-erro').hidden = true;
  contar($('item-texto'), $('item-conta'), E.d.limits.item);
  $('dlg-item').showModal();
  $('item-nome').focus();
}

const exigir = (condicao, mensagem) => { if (!condicao) throw new Error(mensagem); };
function aoEnviar(idDialogo, idErro, enviar) {
  const dlg = $(idDialogo), form = dlg.querySelector('form');
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const erro = $(idErro), ok = form.querySelector('[type="submit"]');
    erro.hidden = true; ok.disabled = true;
    try { const mensagem = await enviar(); dlg.close(); if (mensagem) recado(mensagem); }
    catch (falha) { erro.textContent = falha.message; erro.hidden = false; }
    finally { ok.disabled = false; }
  });
}
for (const b of document.querySelectorAll('[data-fechar]')) b.onclick = () => b.closest('dialog').close();

aoEnviar('dlg-item', 'item-erro', async () => {
  const aberto = E.itemAberto, i = aberto ? aberto.item : null;
  const corpo = {
    title: $('item-nome').value.trim(), description: $('item-descricao').value.trim(), bank: $('item-banco').value.trim(), category: $('item-categoria').value.trim(),
    keywords: $('item-chaves').value.trim(), content: $('item-texto').value.trim(), aiInstructions: $('item-instrucoes').value.trim(), notes: $('item-obs').value.trim(),
    enabled: $('item-status').value === '1',
  };
  if (!i || i.kind !== 'document') corpo.kind = $('item-tipo').value;
  exigir(corpo.title, 'Informe o nome.');
  const semTexto = !corpo.content;
  exigir(!semTexto || (i && i.kind === 'document'), 'Escreva o texto: é ele que a IA lê.');
  exigir(corpo.content.length <= E.d.limits.item, `O texto passou do limite de ${mil(E.d.limits.item)} caracteres. Divida em partes.`);
  if (semTexto) corpo.enabled = false;
  const salvo = i ? await api('PUT', `/neural/items/${i.id}`, corpo) : await api('POST', '/neural/items', corpo);
  await carregar();
  if (E.vista === 'mapa') verNoMapa(salvo.id); else if (E.gaveta === salvo.id) gavetaItem(salvo.id);
  return `"${salvo.title}" foi salvo${salvo.enabled ? ' e a IA já responde com ele' : ' (desativado: a IA não usa)'}.`;
});

function confirmar({ titulo, texto: mensagem, botao: rotulo }) {
  return new Promise((resolver) => {
    const dlg = $('dlg-confirmar'), form = dlg.querySelector('form');
    $('conf-titulo').textContent = titulo; $('conf-texto').textContent = mensagem; $('conf-ok').textContent = rotulo;
    let sim = false;
    form.onsubmit = (e) => { e.preventDefault(); sim = true; dlg.close(); };
    dlg.onclose = () => resolver(sim);
    dlg.showModal();
  });
}

// ---------------------------------------------------------------- vínculos
function abrirLigar(origem) {
  E.ligando = origem; E.ligarEscolha = null;
  $('ligar-titulo').textContent = `Vincular "${origem.title}"`;
  $('ligar-busca').value = ''; $('ligar-erro').hidden = true;
  pintarLigar();
  $('dlg-ligar').showModal();
  $('ligar-busca').focus();
}
function pintarLigar() {
  const q = semAcento($('ligar-busca').value), origem = E.ligando;
  const candidatos = E.d.items.filter((i) => i.id !== origem.id && (!q || semAcento(`${i.title} ${i.bank} ${i.category}`).includes(q))).slice(0, 60);
  $('ligar-lista').replaceChildren(...(candidatos.length ? candidatos.map((i) => {
    const b = el('button', { type: 'button', role: 'option', 'aria-selected': String(E.ligarEscolha === i.id) }, el('i', { className: 'ponto', style: `width:8px;height:8px;border-radius:50%;background:${TIPOS[i.kind].cor}` }), el('span', { textContent: i.title }));
    b.onclick = () => { E.ligarEscolha = i.id; pintarLigar(); };
    return b;
  }) : [el('p', { className: 'nota', textContent: 'Nenhum outro conhecimento com esse nome.' })]));
}
$('ligar-busca').oninput = pintarLigar;
aoEnviar('dlg-ligar', 'ligar-erro', async () => {
  exigir(E.ligarEscolha, 'Escolha o conhecimento a vincular.');
  const origem = E.ligando;
  await api('POST', '/neural/links', { from: origem.id, to: E.ligarEscolha });
  await carregar();
  if (E.gaveta === origem.id) gavetaItem(origem.id);
  return 'Conhecimentos vinculados. A ligação aparece tracejada no mapa.';
});

// -------------------------------------------------------------- documentos
function escolherArquivo(substituirId) { E.substituir = substituirId; $('arquivo').multiple = !substituirId; $('arquivo').value = ''; $('arquivo').click(); }
$('arquivo').onchange = () => { const arquivos = [...$('arquivo').files]; if (arquivos.length) enviarArquivos(arquivos, E.substituir); };

function enviarUm(arquivo, substituirId, progresso) {
  return new Promise((resolver, rejeitar) => {
    const x = new XMLHttpRequest();
    x.open('POST', `/api/v1/neural/documents${substituirId ? `?replace=${encodeURIComponent(substituirId)}` : ''}`);
    x.setRequestHeader('X-Pontual', '1'); x.setRequestHeader('X-File-Name', encodeURIComponent(arquivo.name)); x.setRequestHeader('Content-Type', 'application/octet-stream');
    x.upload.onprogress = (e) => { if (e.lengthComputable) progresso(e.loaded / e.total); };
    x.onerror = () => rejeitar(new Error('A conexão com o servidor caiu durante o envio.'));
    x.onload = () => {
      let dados = null; try { dados = JSON.parse(x.responseText); } catch { dados = null; }
      if (x.status >= 200 && x.status < 300 && dados) resolver(dados);
      else rejeitar(new Error((dados && dados.error) || (x.status === 413 ? 'O arquivo é grande demais.' : `O servidor respondeu com o erro ${x.status}.`)));
    };
    x.send(arquivo);
  });
}

async function enviarArquivos(arquivos, substituirId) {
  const limites = E.d.limits, enviados = [];
  for (const arquivo of arquivos) {
    const envio = { nome: arquivo.name, p: 0, estado: 'enviando', nota: 'Enviando…' };
    E.envios.unshift(envio); pintarEnvios();
    const extensao = arquivo.name.slice(arquivo.name.lastIndexOf('.')).toLowerCase();
    try {
      exigir(limites.extensions.includes(extensao), 'Formato não aceito. Envie PDF, DOCX ou TXT.');
      exigir(arquivo.size > 0, 'O arquivo está vazio.');
      exigir(arquivo.size <= limites.upload, `O arquivo tem ${tamanho(arquivo.size)}; o máximo é ${tamanho(limites.upload)}.`);
      const doc = await enviarUm(arquivo, substituirId, (p) => { envio.p = p; envio.nota = p >= 1 ? 'Lendo o texto…' : `${Math.round(p * 100)}%`; pintarEnvios(); });
      envio.p = 1; envio.estado = 'ok';
      envio.nota = doc.fileStatus === 'processed' ? `Texto extraído: ${mil(doc.chars)} caracteres` : 'Guardado sem texto (PDF escaneado)';
      enviados.push(doc);
    } catch (e) { envio.estado = 'falha'; envio.nota = e.message; }
    pintarEnvios();
  }
  E.envios = E.envios.slice(0, 8);
  if (!enviados.length) { if (E.vista !== 'documentos') recado(E.envios[0].nota, true); return; }
  await carregar();
  const escaneados = enviados.filter((d) => d.fileStatus === 'no_text').length;
  recado(substituirId ? `Arquivo de "${enviados[0].title}" atualizado.` : `${plural(enviados.length, 'documento enviado', 'documentos enviados')}${escaneados ? `; ${escaneados} sem texto (escaneado), guardado e desativado até o leitor de PDF ser conectado` : ''}. Complete o banco e a categoria para organizar o mapa.`, false);
  if (enviados.length === 1) {
    // Um documento só: abre o cadastro para completar descrição, banco e categoria.
    try { abrirFormulario(await api('GET', `/neural/items/${enviados[0].id}`)); } catch { /* o documento já está na lista */ }
  }
}
function pintarEnvios() {
  const alvo = $('envios');
  if (!alvo) return;
  alvo.replaceChildren(...E.envios.map((e) => el('div', { className: `envio ${e.estado}` }, el('span', { textContent: e.nome }), el('em', { textContent: e.nota }), e.estado === 'enviando' ? el('i', { style: `--p:${Math.round(e.p * 100)}%` }) : null)));
}

// ================================================================== listas
function filtrosDe(vista) {
  if (!E.listas[vista]) E.listas[vista] = { q: '', tipo: '', banco: '', categoria: '', estado: '', origem: '', ordem: 'atualizado' };
  return E.listas[vista];
}
function desenharLista(vista) {
  const f = filtrosDe(vista), alvo = $(`v-${vista}`), fixo = { roteiros: 'script', documentos: 'document' }[vista] || null;
  const universo = fixo ? itensDe(fixo) : E.d.items;
  const unicos = (campo) => [...new Set(universo.map((i) => i[campo]).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'pt-BR'));
  const escolha = (chave, rotulo, opcoes) => {
    const s = el('select', { className: 'campo', 'aria-label': rotulo }, el('option', { value: '', textContent: rotulo }), ...opcoes.map(([v, r]) => el('option', { value: v, textContent: r, selected: f[chave] === v })));
    s.onchange = () => { f[chave] = s.value; pintarTabela(vista); };
    return s;
  };
  const busca = el('input', { className: 'campo cresce', type: 'search', placeholder: 'Filtrar por nome, banco, categoria ou palavra-chave', 'aria-label': 'Filtrar a lista', value: f.q });
  busca.oninput = () => { f.q = busca.value; pintarTabela(vista); };
  const ordem = el('select', { className: 'campo', 'aria-label': 'Ordenar' }, ...[['atualizado', 'Mais recentes'], ['nome', 'Nome'], ['tamanho', 'Maiores'], ['criado', 'Criação']].map(([v, r]) => el('option', { value: v, textContent: `Ordem: ${r}`, selected: f.ordem === v })));
  ordem.onchange = () => { f.ordem = ordem.value; pintarTabela(vista); };

  const acoes = [];
  if (vista !== 'documentos') { const b = botao('Novo roteiro', 'mais', 'btn'); b.onclick = () => abrirFormulario(null, 'script'); acoes.push(b); }
  if (vista === 'base') { const b = botao('Novo conhecimento', 'mais', 'btn claro'); b.onclick = () => abrirFormulario(null, 'knowledge'); acoes.push(b); }
  if (vista !== 'roteiros') { const b = botao('Enviar documento', 'enviar', vista === 'documentos' ? 'btn' : 'btn claro'); b.onclick = () => escolherArquivo(null); acoes.push(b); }

  const titulos = { roteiros: ['Roteiros cadastrados', 'Cada roteiro ativo é lido pela IA. Clique numa linha para ver os detalhes e os vínculos.'], documentos: ['Documentos da base', 'O texto extraído de cada arquivo é o que a IA lê. O arquivo original fica guardado.'], base: ['Explorador de conhecimento', 'Roteiros, documentos e conhecimentos operacionais, com todos os filtros.'] };
  const partes = [];
  if (vista === 'base' && E.d.suggestions.pending) {
    const ver = botao('Revisar', 'aprender'); ver.onclick = () => ir('aprendizado');
    partes.push(el('div', { className: 'cartao' }, el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: `${plural(E.d.suggestions.pending, 'conhecimento pendente', 'conhecimentos pendentes')} de aprovação` }), el('p', { textContent: 'Sugestões das atendentes e da análise das conversas. Só entram na base depois de aprovadas.' })), ver)));
  }
  const cartao = el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: titulos[vista][0] }), el('p', { textContent: titulos[vista][1] })), ...acoes));
  if (vista === 'documentos') {
    const solte = el('div', { className: 'solte', role: 'button', tabIndex: 0, 'aria-label': 'Enviar documentos: solte os arquivos aqui ou pressione Enter para escolher' },
      ic('enviar', 30), el('b', { textContent: 'Arraste os arquivos para cá' }), el('span', { textContent: `ou clique para escolher. PDF, DOCX ou TXT, até ${tamanho(E.d.limits.upload)} cada.` }),
      el('span', { className: 'nota', textContent: 'PDF escaneado (imagem) fica guardado e desativado até o leitor de PDF escaneado ser conectado.' }));
    solte.onclick = () => escolherArquivo(null);
    solte.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); escolherArquivo(null); } };
    solte.ondragover = (e) => { e.preventDefault(); solte.classList.add('sobre'); };
    solte.ondragleave = () => solte.classList.remove('sobre');
    solte.ondrop = (e) => { e.preventDefault(); solte.classList.remove('sobre'); const arquivos = [...e.dataTransfer.files]; if (arquivos.length) enviarArquivos(arquivos, null); };
    cartao.append(solte, el('div', { className: 'envios', id: 'envios' }));
  }
  cartao.append(el('div', { className: 'ferramentas' }, busca,
    fixo ? null : escolha('tipo', 'Todos os tipos', ['script', 'document', 'knowledge'].map((t) => [t, TIPOS[t].varios])),
    escolha('banco', 'Todos os bancos', unicos('bank').map((b) => [b, b])),
    escolha('categoria', 'Todas as categorias', unicos('category').map((c) => [c, c])),
    escolha('estado', 'Todas as situações', [['active', 'Ativos'], ['disabled', 'Desativados'], ['pending', 'Aguardando leitor de PDF'], ['instrucoes', 'Com instruções para a IA']]),
    fixo ? null : escolha('origem', 'Todas as origens', Object.entries(ORIGENS)),
    ordem), el('div', { className: 'rolagem-x' }, el('table', { id: `tb-${vista}` })));
  partes.push(cartao);
  alvo.replaceChildren(...partes);
  pintarEnvios();
  pintarTabela(vista);
}

function pintarTabela(vista) {
  const f = filtrosDe(vista), fixo = { roteiros: 'script', documentos: 'document' }[vista] || null, q = semAcento(f.q);
  let lista = (fixo ? itensDe(fixo) : E.d.items).filter((i) =>
    (!f.tipo || i.kind === f.tipo) && (!f.banco || i.bank === f.banco) && (!f.categoria || i.category === f.categoria) && (!f.origem || i.source === f.origem)
    && (!f.estado || (f.estado === 'instrucoes' ? i.hasInstructions : estadoDe(i) === f.estado))
    && (!q || semAcento(`${i.title} ${i.description} ${i.bank} ${i.category} ${i.keywords} ${i.fileName}`).includes(q)));
  const ordens = { atualizado: (a, b) => new Date(b.updatedAt) - new Date(a.updatedAt), nome: (a, b) => a.title.localeCompare(b.title, 'pt-BR'), tamanho: (a, b) => b.chars - a.chars, criado: (a, b) => new Date(b.createdAt) - new Date(a.createdAt) };
  lista = lista.slice().sort(ordens[f.ordem]);

  const colunas = ['Nome', fixo ? null : 'Tipo', 'Banco', 'Categoria', 'Situação', 'Tamanho', 'Atualizado', ''].filter((c) => c !== null);
  const cab = el('thead', {}, el('tr', {}, ...colunas.map((c) => el('th', { scope: 'col', className: c === 'Tamanho' ? 'num' : '', textContent: c }))));
  const tabela = $(`tb-${vista}`);
  if (!lista.length) {
    const nenhum = !(fixo ? itensDe(fixo) : E.d.items).length;
    const frases = { roteiros: 'Use "Novo roteiro" para cadastrar o primeiro.', documentos: 'Arraste um PDF, DOCX ou TXT para a área acima.', base: 'Cadastre um roteiro ou envie um documento para começar.' };
    tabela.replaceChildren(cab, el('tbody', {}, el('tr', {}, el('td', { colSpan: colunas.length }, nenhum ? vazio('Nada cadastrado ainda', frases[vista]) : vazio('Nada com esses filtros', 'Limpe a busca ou troque os filtros.')))));
    return;
  }
  tabela.replaceChildren(cab, el('tbody', {}, ...lista.map((i) => {
    const estado = estadoDe(i);
    const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(i.enabled), 'aria-label': `${i.enabled ? 'Desativar' : 'Ativar'} ${i.title}`, disabled: !i.chars });
    chave.onclick = (e) => { e.stopPropagation(); agir(chave, () => api('PUT', `/neural/items/${i.id}`, { enabled: !i.enabled }), `${i.title}: ${i.enabled ? 'desativado' : 'ativado'}.`); };
    const mapa = iconeBotao('mira', `Ver ${i.title} no mapa`); mapa.onclick = (e) => { e.stopPropagation(); verNoMapa(i.id); };
    const editar = iconeBotao('editar', `Editar ${i.title}`); editar.onclick = async (e) => { e.stopPropagation(); try { abrirFormulario(await api('GET', `/neural/items/${i.id}`)); } catch (erro) { recado(erro.message, true); } };
    const excluir = iconeBotao('lixo', `Excluir ${i.title}`, 'perigo'); excluir.onclick = (e) => { e.stopPropagation(); excluirItem(i); };
    const linha = el('tr', { className: 'clicavel' },
      el('td', { className: 'nome' }, el('b', { textContent: i.title }), el('small', { textContent: i.description || (i.fileName ? i.fileName : '') })),
      fixo ? null : el('td', {}, selo(i.kind, TIPOS[i.kind].um)),
      el('td', { textContent: i.bank || '—' }), el('td', { textContent: i.category || '—' }),
      el('td', {}, el('div', { style: 'display:flex;align-items:center;gap:10px' }, chave, selo(`${estado} liso`, ESTADOS[estado]))),
      el('td', { className: 'num', textContent: mil(i.chars) }),
      el('td', { textContent: quando(i.updatedAt), title: i.updatedBy ? `por ${i.updatedBy}` : '' }),
      el('td', { className: 'acoes-linha' }, mapa, editar, excluir));
    linha.onclick = () => { gavetaItem(i.id); };
    return linha;
  })));
}

// ============================================================= aprendizado
function corpoSugestao(s, naGaveta) {
  const origem = s.source === 'attendant' ? `Enviada por ${s.attendant || 'uma atendente'} pelo chat`
    : s.source === 'import' ? `Tirada do histórico de conversas com clientes${s.attendant ? ` (${s.attendant})` : ''}`
    : `Encontrada pela IA nas conversas${s.attendant ? ` de ${s.attendant}` : ''}`;
  const partes = [
    el('h3', { textContent: s.title, style: naGaveta ? 'font:600 19px Sora,Inter,sans-serif;letter-spacing:0;text-transform:none;color:var(--texto)' : null }),
    el('p', { className: 'nota', textContent: `${origem} · ${quando(s.updatedAt)}${s.occurrences > 1 ? ` · apareceu ${mil(s.occurrences)} vezes` : ''}${s.bank ? ` · ${s.bank}` : ''}` }),
    s.detail ? el('p', { textContent: s.detail }) : null,
    s.question ? el('div', { className: 'citacao' }, el('b', { textContent: 'Pergunta' }), s.question) : null,
    s.answer ? el('div', { className: 'citacao' }, el('b', { textContent: 'Resposta que a assistente deu' }), s.answer) : null,
    s.proposedContent ? el('div', { className: 'citacao' }, el('b', { textContent: 'Texto sugerido' }), s.proposedContent) : null,
  ];
  const botoes = el('div', { className: 'botoes' });
  if (s.status === 'pending') {
    const revisar = botao('Revisar e aprovar', 'editar', 'btn mini'); revisar.onclick = () => abrirSugestao(s);
    const rejeitar = botao('Rejeitar', 'x');
    rejeitar.onclick = async () => { if (await confirmar({ titulo: 'Rejeitar esta sugestão?', texto: 'Ela sai da fila e a IA continua sem esta informação. Dá para reabrir depois.', botao: 'Rejeitar' })) { await agir(rejeitar, () => api('POST', `/neural/suggestions/${s.id}/reject`), 'Sugestão rejeitada.'); if (naGaveta) fecharGaveta(); } };
    botoes.append(revisar, rejeitar);
  } else {
    const reabrir = botao('Reabrir', 'voltar'); reabrir.onclick = async () => { await agir(reabrir, () => api('POST', `/neural/suggestions/${s.id}/reopen`), 'Sugestão reaberta: voltou para a fila.'); if (naGaveta) fecharGaveta(); };
    botoes.append(reabrir);
    if (s.itemId && item(s.itemId)) { const ver = botao('Ver o conhecimento', 'mira'); ver.onclick = () => verNoMapa(s.itemId); botoes.append(ver); }
    partes.push(el('p', { className: 'nota', textContent: `${SITUACAO[s.status]} por ${s.reviewedBy || '—'} em ${quando(s.reviewedAt)}.` }));
  }
  partes.push(botoes);
  return partes;
}

function desenharAprendizado() {
  const d = E.d, alvo = $('v-aprendizado');
  const numeros = el('div', { className: 'numeros' },
    el('div', { className: `numero${d.suggestions.pending ? ' destaque' : ''}` }, el('b', { textContent: mil(d.suggestions.pending) }), el('span', { textContent: 'Sugestões pendentes' }), el('small', { textContent: 'aguardando a sua revisão' })),
    el('div', { className: 'numero' }, el('b', { textContent: mil(d.suggestions.approved) }), el('span', { textContent: 'Aprovadas' }), el('small', { textContent: 'viraram conhecimento' })),
    el('div', { className: 'numero' }, el('b', { textContent: mil(d.suggestions.rejected) }), el('span', { textContent: 'Rejeitadas' })),
    el('div', { className: 'numero' }, el('b', { textContent: mil(d.feedback.helpful) }), el('span', { textContent: 'Respostas que ajudaram' }), el('small', { textContent: 'avaliadas pelas atendentes' })),
    el('div', { className: 'numero' }, el('b', { textContent: mil(d.feedback.notHelpful) }), el('span', { textContent: 'Respostas corrigidas' }), el('small', { textContent: 'marcadas como "não ajudou"' })));

  const auto = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(d.settings.autoAnalysis), 'aria-label': 'Análise automática das conversas' });
  auto.onclick = () => agir(auto, () => api('PUT', '/neural/settings', { autoAnalysis: !d.settings.autoAnalysis }), `Análise automática ${d.settings.autoAnalysis ? 'desligada' : 'ligada'}.`);

  const zap = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(d.settings.whatsappLearning), 'aria-label': 'Aprender com as conversas do WhatsApp' });
  zap.onclick = async () => {
    if (d.settings.whatsappLearning && !(await confirmar({ titulo: 'Parar de aprender com o WhatsApp?', texto: 'Os Terminais deixam de registrar as conversas e o que está na fila, ainda sem análise, é apagado. Os perfis e as sugestões já criados continuam.', botao: 'Desligar' }))) return;
    agir(zap, () => api('PUT', '/neural/settings', { whatsappLearning: !d.settings.whatsappLearning }), `Aprendizado com o WhatsApp ${d.settings.whatsappLearning ? 'desligado' : 'ligado'}.`);
  };

  const segmento = el('div', { className: 'segmento', role: 'group', 'aria-label': 'Situação das sugestões' }, ...Object.entries({ pending: 'Pendentes', approved: 'Aprovadas', rejected: 'Rejeitadas' }).map(([v, r]) => {
    const b = el('button', { type: 'button', 'aria-pressed': String(E.situacao === v), textContent: r });
    b.onclick = () => { E.situacao = v; desenharAprendizado(); };
    return b;
  }));
  const fila = el('div', { className: 'sugestoes', id: 'fila' }, el('p', { className: 'nota', textContent: 'Carregando…' }));

  const pessoas = d.attendants.map((a) => {
    const ver = botao(a.hasProfile ? 'Ver o perfil' : 'Abrir', 'abrir'); ver.onclick = () => verNoMapa(`a:${a.deviceId}`);
    const temNovo = a.newMessages > 0 || a.whatsappPending > 0;
    const analisar = botao('Analisar agora', 'raio', 'btn mini'); analisar.disabled = !temNovo || !d.settings.hasKey;
    analisar.title = !d.settings.hasKey ? 'Cadastre a chave da IA primeiro.' : temNovo ? 'Lê só o que é novo e atualiza o perfil.' : 'Não há nada novo.';
    analisar.onclick = () => analisarAtendente(a.deviceId, a.name, analisar);
    const falta = botaoFalta(a.deviceId, a.name, a.absent);
    return el('div', { className: 'pessoa' },
      el('div', {}, el('b', { textContent: a.name }), el('small', { textContent: `${plural(a.questions, 'pergunta', 'perguntas')} à assistente · ${a.newMessages ? plural(a.newMessages, 'fala nova', 'falas novas') : 'nenhuma fala nova'}` }),
        el('small', { textContent: a.whatsappPending ? `${plural(a.whatsappPending, 'conversa de WhatsApp', 'conversas de WhatsApp')} na fila da análise` : 'Nenhuma conversa de WhatsApp na fila' }),
        el('small', { textContent: a.hasProfile ? `Perfil atualizado: ${quando(a.analyzedAt)}` : 'Perfil ainda não montado' }),
        a.absent ? el('small', { className: 'aviso', textContent: 'Faltou hoje: a IA está respondendo os clientes dela' }) : null,
        a.coverage === 'test' || a.coveragePending ? el('small', { className: a.coveragePending ? 'aviso' : null, textContent: `${a.coverage === 'test' ? 'Na falta, a IA só escreve (ensaio)' : 'Cobertura de ausência'}${a.coveragePending ? ` · ${plural(a.coveragePending, 'resposta esperando', 'respostas esperando')} aprovação` : ''}` }) : null),
      el('div', { className: 'botoes' }, falta, analisar, ver));
  });

  alvo.replaceChildren(numeros,
    el('div', { className: 'cartao' },
      el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Sugestões de conhecimento' }), el('p', { textContent: 'Correções enviadas pelas atendentes e lacunas que a IA encontrou nas conversas. Você revisa, edita e decide.' })), segmento),
      fila),
    el('div', { className: 'cartao' },
      el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Perfil de cada atendente' }), el('p', { textContent: 'A IA lê as conversas de cada atendente com os clientes no WhatsApp e as perguntas que ela faz à assistente, atualiza o perfil de atendimento e aponta o que falta na base.' }))),
      el('div', { className: 'cartao-corpo', style: 'display:grid;gap:16px' },
        el('div', { className: 'linha-opcao' }, el('div', {}, el('b', { textContent: 'Aprender com o WhatsApp' }),
          el('small', { textContent: 'O Terminal (versão 2.4.0 ou mais nova) registra cada conversa que a atendente abre no WhatsApp. A conversa fica no servidor só até ser analisada e depois é apagada; o que não for analisado em 7 dias também.' })), zap),
        el('div', { className: 'linha-opcao' }, el('div', {}, el('b', { textContent: 'Análise automática' }),
          el('small', { textContent: 'Uma vez por dia por atendente, quando há conversas novas e a última já terminou há 20 minutos. Desligada, a análise roda só pelo botão.' })), auto)),
      pessoas.length ? el('div', { className: 'pessoas' }, ...pessoas) : vazio('Nenhuma atendente ainda', 'As máquinas aprovadas na Central do TI aparecem aqui.')));
  pintarFila();
}

async function pintarFila() {
  let todas;
  try { todas = await buscarSugestoes(); } catch (e) { const f = $('fila'); if (f) f.replaceChildren(el('div', { className: 'vazio erro', textContent: e.message })); return; }
  const fila = $('fila');
  if (!fila || E.vista !== 'aprendizado') return;
  const lista = todas.filter((s) => s.status === E.situacao);
  if (!lista.length) {
    const frases = { pending: ['Nenhuma sugestão pendente', 'Elas chegam quando uma atendente marca "não ajudou" numa resposta e escreve a correção, ou quando a IA analisa as conversas.'], approved: ['Nenhuma sugestão aprovada ainda', ''], rejected: ['Nenhuma sugestão rejeitada', ''] };
    fila.replaceChildren(vazio(...frases[E.situacao]));
    return;
  }
  fila.replaceChildren(...lista.map((s) => el('article', { className: 'sugestao' },
    el('header', {}, selo('suggestion', SUGESTOES[s.kind] || 'Sugestão'), selo(`${s.status} liso`, SITUACAO[s.status])), ...corpoSugestao(s, false))));
}

function ajustarDestino() {
  const existente = $('sug-destino').value === 'existente';
  $('sug-item-caixa').hidden = !existente;
  for (const id of ['sug-nome-caixa', 'sug-banco-caixa', 'sug-categoria-caixa']) $(id).hidden = existente;
  $('sug-dica').textContent = existente ? 'Este texto é acrescentado ao fim do conhecimento escolhido. Vazio = só vincula a sugestão a ele.' : 'Este texto vira um conhecimento novo, ativo, que a IA usa na pergunta seguinte.';
}
$('sug-destino').onchange = ajustarDestino;

function abrirSugestao(s) {
  E.sugestaoAberta = s;
  opcoesDeLista();
  $('sug-origem').replaceChildren(...[
    s.question ? el('div', { className: 'citacao' }, el('b', { textContent: 'Pergunta' }), s.question) : null,
    s.answer ? el('div', { className: 'citacao' }, el('b', { textContent: 'Resposta que a assistente deu' }), s.answer) : null,
    s.detail ? el('div', { className: 'citacao' }, el('b', { textContent: s.kind === 'correction' ? 'O que a atendente escreveu' : 'O que a análise encontrou' }), s.detail) : null,
  ].filter(Boolean));
  $('sug-destino').value = 'knowledge';
  $('sug-item').replaceChildren(...E.d.items.slice().sort((a, b) => a.title.localeCompare(b.title, 'pt-BR')).map((i) => el('option', { value: i.id, textContent: `${i.title} (${TIPOS[i.kind].um.toLowerCase()})`, selected: i.id === s.itemId })));
  $('sug-nome').value = s.title; $('sug-banco').value = s.bank; $('sug-categoria').value = s.category;
  // Numa correção, o que a atendente escreveu é o ponto de partida; o administrador reescreve como regra.
  $('sug-texto').value = s.proposedContent || (s.kind === 'correction' ? s.detail : '');
  $('sug-erro').hidden = true;
  ajustarDestino();
  $('dlg-sugestao').showModal();
  $('sug-texto').focus();
}
$('sug-rascunho').onclick = async () => {
  const s = E.sugestaoAberta;
  try {
    await api('PUT', `/neural/suggestions/${s.id}`, { title: $('sug-nome').value.trim() || s.title, proposedContent: $('sug-texto').value.trim(), bank: $('sug-banco').value.trim(), category: $('sug-categoria').value.trim() });
    $('dlg-sugestao').close();
    await carregar();
    recado('Rascunho salvo. A sugestão continua pendente.');
  } catch (e) { $('sug-erro').textContent = e.message; $('sug-erro').hidden = false; }
};
aoEnviar('dlg-sugestao', 'sug-erro', async () => {
  const s = E.sugestaoAberta, destino = $('sug-destino').value, conteudo = $('sug-texto').value.trim();
  const corpo = destino === 'existente'
    ? { itemId: $('sug-item').value, content: conteudo }
    : { createKind: destino, title: $('sug-nome').value.trim(), bank: $('sug-banco').value.trim(), category: $('sug-categoria').value.trim(), content: conteudo };
  exigir(destino !== 'existente' || corpo.itemId, 'Escolha o conhecimento que recebe o texto.');
  exigir(destino === 'existente' || corpo.title, 'Informe o nome do novo conhecimento.');
  exigir(destino === 'existente' || conteudo, 'Escreva o texto que a IA vai usar.');
  const r = await api('POST', `/neural/suggestions/${s.id}/approve`, corpo);
  await carregar();
  if (E.gaveta === `s:${s.id}`) fecharGaveta();
  return `Sugestão aprovada: "${r.item.title}" ${destino === 'existente' ? 'foi atualizado' : 'entrou na base'} e a IA já usa.`;
});

// ============================================================= visão geral
function desenharVisao() {
  const d = E.d, itens = d.items, docs = itensDe('document');
  const numero = (valor, rotulo, nota, destaque) => el('div', { className: `numero${destaque ? ' destaque' : ''}` }, el('b', { textContent: mil(valor) }), el('span', { textContent: rotulo }), nota ? el('small', { textContent: nota }) : null);
  const semTexto = docs.filter((x) => x.fileStatus === 'no_text').length;
  const perguntas = d.attendants.reduce((s, a) => s + a.questions, 0);
  const numeros = el('div', { className: 'numeros' },
    numero(itensDe('script').length, 'Roteiros'), numero(docs.length, 'Documentos', `${mil(docs.filter((x) => x.fileStatus === 'processed').length)} com texto extraído`),
    numero(itensDe('knowledge').length, 'Conhecimentos operacionais'), numero(itens.filter((i) => i.enabled).length, 'Ativos', `de ${mil(itens.length)} no total`),
    numero(d.suggestions.pending, 'Pendentes de aprovação', 'sugestões de aprendizado', d.suggestions.pending > 0),
    numero(semTexto, 'Documentos sem texto', 'PDF escaneado, aguardando leitor', semTexto > 0),
    numero(d.links.length, 'Ligações'), numero(perguntas, 'Perguntas das atendentes', `${mil(d.attendants.filter((a) => a.hasProfile).length)} com perfil montado`));

  const cheio = Math.min(100, Math.round((d.brain.enabledChars / d.brain.inlineBudget) * 100));
  const leitura = el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Como a IA lê a base' }), el('p', { textContent: d.brain.mode === 'inline' ? 'Leitura completa: tudo o que está ativo vai em cada pergunta.' : 'Índice + consulta: a IA abre só o que a pergunta pede.' }))),
    el('div', { className: 'cartao-corpo' },
      el('div', { className: `medidor${cheio >= 100 ? ' cheio' : ''}`, style: `--p:${cheio}%` }),
      el('p', { className: 'nota', textContent: `${mil(d.brain.enabledChars)} de ${mil(d.brain.inlineBudget)} caracteres da leitura completa (${cheio}%). Quando a base passar disso, a IA muda sozinha para o índice, para a pergunta não ficar cara. O teto total é de ${mil(d.brain.limitChars)} caracteres.` }),
      el('p', { className: 'nota', textContent: `Modelo: ${d.settings.model}. Orientações gerais: ${d.settings.generalInstructions ? `${mil(d.settings.generalInstructions)} caracteres` : 'nenhuma escrita'} (editadas em Central do TI → Assistente).` })));

  const barras = (campo, vazioTexto) => {
    const conta = new Map();
    for (const i of itens) if (i[campo]) conta.set(i[campo], (conta.get(i[campo]) || 0) + 1);
    const lista = [...conta].sort((a, b) => b[1] - a[1]).slice(0, 8), maior = lista.length ? lista[0][1] : 1;
    return lista.length ? el('div', { className: 'barras' }, ...lista.map(([nome, n]) => el('div', {}, el('span', { textContent: nome }), el('b', { textContent: mil(n) }), el('i', { style: `--p:${Math.round((n / maior) * 100)}%` })))) : el('p', { className: 'nota', textContent: vazioTexto });
  };
  const cartao = (titulo, frase, corpo) => el('div', { className: 'cartao' }, el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: titulo }), frase ? el('p', { textContent: frase }) : null)), el('div', { className: 'cartao-corpo' }, corpo));

  const recentes = itens.slice().sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt)).slice(0, 8).map((i) => {
    const b = el('button', { type: 'button' }, el('i', { className: 'ponto', style: `--c:${TIPOS[i.kind].cor}` }), el('span', { className: 'nome', textContent: i.title }), el('small', { className: 'nota', textContent: quando(i.updatedAt) }));
    b.onclick = () => verNoMapa(i.id);
    return b;
  });
  const novos = itens.slice().sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt)).slice(0, 8).map((i) => {
    const b = el('button', { type: 'button' }, el('i', { className: 'ponto', style: `--c:${TIPOS[i.kind].cor}` }), el('span', { className: 'nome', textContent: i.title }), el('small', { className: 'nota', textContent: ORIGENS[i.source] || i.source }));
    b.onclick = () => verNoMapa(i.id);
    return b;
  });
  const sugestoes = d.pendingSuggestions.slice(0, 6).map((s) => {
    const b = el('button', { type: 'button' }, el('i', { className: 'ponto', style: `--c:${MAPA_CORES.suggestion}` }), el('span', { className: 'nome', textContent: s.title }), s.occurrences > 1 ? el('small', { className: 'nota', textContent: `${mil(s.occurrences)}×` }) : null);
    b.onclick = () => ir('aprendizado');
    return b;
  });

  $('v-visao').replaceChildren(numeros, leitura, el('div', { className: 'duas' },
    cartao('Últimas atualizações', '', recentes.length ? el('div', { className: 'lista-simples' }, ...recentes) : vazio('Nada cadastrado ainda', '')),
    cartao('Adicionados recentemente', '', novos.length ? el('div', { className: 'lista-simples' }, ...novos) : vazio('Nada cadastrado ainda', '')),
    cartao('Categorias mais usadas', '', barras('category', 'Nenhum conhecimento tem categoria definida ainda.')),
    cartao('Bancos e assuntos', '', barras('bank', 'Nenhum conhecimento tem banco definido ainda.')),
    cartao('Sugestões de aprendizado', 'As pendentes que mais se repetem.', sugestoes.length ? el('div', { className: 'lista-simples' }, ...sugestoes) : el('p', { className: 'nota', textContent: 'Nenhuma sugestão pendente.' }))));
}

// =================================================================== busca
let relogioBusca = null, versaoBusca = 0;
function resultado(titulo, nota, aoClicar) {
  const b = el('button', { type: 'button', role: 'option' }, el('span', { textContent: titulo }), nota ? el('small', { textContent: nota }) : null);
  b.onclick = () => { $('busca-lista').hidden = true; aoClicar(); };
  return b;
}
async function buscar() {
  const q = $('busca').value.trim(), lista = $('busca-lista'), versao = ++versaoBusca;
  if (q.length < 2) { lista.hidden = true; return; }
  const dobra = semAcento(q);
  const pessoas = E.d.attendants.filter((a) => semAcento(a.name).includes(dobra)).slice(0, 5);
  const grupos = [...new Set(E.d.items.flatMap((i) => [i.bank, i.category]).filter((v) => v && semAcento(v).includes(dobra)))].slice(0, 5);
  let r;
  try { r = await api('GET', `/neural/search?q=${encodeURIComponent(q)}`); } catch (e) { if (versao === versaoBusca) { lista.replaceChildren(el('p', { className: 'vazio erro', textContent: e.message })); lista.hidden = false; } return; }
  if (versao !== versaoBusca) return;
  const partes = [];
  if (r.items.length) partes.push(el('h3', { textContent: 'Conhecimentos' }), ...r.items.slice(0, 12).map((i) => resultado(i.title, `${TIPOS[i.kind].um}${i.bank ? ` · ${i.bank}` : ''}${i.enabled ? '' : ' · desativado'}${i.parts > 1 ? ` · parte ${i.part} de ${i.parts}` : ''} — ${i.snippet}`, () => verNoMapa(i.id))));
  if (grupos.length) partes.push(el('h3', { textContent: 'Bancos e categorias' }), ...grupos.map((g) => resultado(g, 'Filtrar a Base de Conhecimento', () => { const f = filtrosDe('base'); Object.assign(f, { q: g, tipo: '', banco: '', categoria: '', estado: '', origem: '' }); ir('base'); })));
  if (pessoas.length) partes.push(el('h3', { textContent: 'Atendentes' }), ...pessoas.map((a) => resultado(a.name, a.hasProfile ? 'Perfil montado pela IA' : 'Sem perfil ainda', () => verNoMapa(`a:${a.deviceId}`))));
  if (r.suggestions.length) partes.push(el('h3', { textContent: 'Sugestões' }), ...r.suggestions.map((s) => resultado(s.title, `${SITUACAO[s.status]} — ${s.snippet}`, () => { E.situacao = s.status; ir('aprendizado'); })));
  lista.replaceChildren(...(partes.length ? partes : [el('p', { className: 'vazio', textContent: 'Nada encontrado na base para essa busca.' })]));
  lista.hidden = false;
}
$('busca').oninput = () => { clearTimeout(relogioBusca); relogioBusca = setTimeout(buscar, 240); };
$('busca').onfocus = () => { if ($('busca').value.trim().length >= 2) buscar(); };
$('busca').onkeydown = (e) => {
  if (e.key === 'ArrowDown') { const primeiro = $('busca-lista').querySelector('button'); if (primeiro) { e.preventDefault(); primeiro.focus(); } }
  if (e.key === 'Enter') { const primeiro = $('busca-lista').querySelector('button'); if (primeiro && !$('busca-lista').hidden) { e.preventDefault(); primeiro.click(); } }
};
$('busca-lista').onkeydown = (e) => {
  const botoes = [...$('busca-lista').querySelectorAll('button')], i = botoes.indexOf(document.activeElement);
  if (e.key === 'ArrowDown' && botoes[i + 1]) { e.preventDefault(); botoes[i + 1].focus(); }
  if (e.key === 'ArrowUp') { e.preventDefault(); (botoes[i - 1] || $('busca')).focus(); }
};
document.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); $('busca').focus(); $('busca').select(); }
  if (e.key === 'Escape' && !document.querySelector('dialog[open]')) {
    if (!$('busca-lista').hidden) { $('busca-lista').hidden = true; return; }
    if (!$('mapa-menu').hidden) { fecharMenu(); return; }
    if (!$('gaveta').hidden) fecharGaveta();
  }
});

iniciar();
