// Central do TI — Terminal Pontual 1.0.
// JavaScript puro, sem etapa de build. Fala com a API descrita em docs/V1-CONTRATO.md.
//
// Regras que valem para o arquivo inteiro:
// - Nada que vem do servidor entra na página como HTML: sempre textContent (via el()).
// - Só salta na tela (toast) a notificação urgente que ainda não tinha sido vista.
//   Confirmações de ações do próprio administrador saem na linha de recado, dentro da tela.
// - A consulta periódica não redesenha formulário aberto e devolve o foco a quem o tinha.
'use strict';

// ================================================================= ícones
// Textos fixos deste arquivo; é o único uso de innerHTML e nunca recebe dado do servidor.
const IC = {
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>', plus: '<path d="M12 5v14M5 12h14"/>', x: '<path d="M6 6l12 12M18 6 6 18"/>',
  check: '<path d="m5 12 5 5 9-10"/>', chevron: '<path d="m9 6 6 6-6 6"/>', baixo: '<path d="m6 9 6 6 6-6"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
  alert: '<path d="M12 4 3 20h18L12 4Z"/><path d="M12 10v4M12 17h.01"/>', bell: '<path d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4l2-2Z"/><path d="M10 20a2 2 0 0 0 4 0"/>',
  monitor: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
  wrench: '<path d="M14.5 6.5a4 4 0 0 0-5.3 5.3L4 17l3 3 5.2-5.2a4 4 0 0 0 5.3-5.3l-2.6 2.6-2-2 2.6-2.6Z"/>',
  lock: '<rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
  globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18"/>', window: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 9h18"/>',
  history: '<path d="M4 12a8 8 0 1 0 2.5-5.8L4 8.5"/><path d="M4 4v4.5h4.5M12 8v4l3 2"/>', ban: '<circle cx="12" cy="12" r="9"/><path d="m6 6 12 12"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>', power: '<path d="M12 4v8"/><path d="M7 7a7 7 0 1 0 10 0"/>',
  offline: '<path d="M3 3l18 18"/><path d="M8.5 16.5a5 5 0 0 1 7 0"/><path d="M5 13a10 10 0 0 1 4-2.6M19 13a10 10 0 0 0-5.6-3"/><path d="M2 9.5a15 15 0 0 1 4.2-2.9M22 9.5a15 15 0 0 0-11.5-4.4"/><path d="M12 20h.01"/>',
  users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0"/><path d="M16 4.7a3.5 3.5 0 0 1 0 6.6M18 14.5a6.5 6.5 0 0 1 3.5 5.5"/>',
  logout: '<path d="M10 4H6a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h4"/><path d="M15 8l4 4-4 4M19 12H9"/>',
  shield: '<path d="M12 3 5 6v6c0 4.5 3 7.5 7 9 4-1.5 7-4.5 7-9V6l-7-3Z"/><path d="m9 12 2.2 2.2L15 10"/>',
  trash: '<path d="M4 7h16M10 11v6M14 11v6"/><path d="M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12"/><path d="M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16v4Z"/><path d="m13.5 6.5 4 4"/>',
  eye: '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/>',
  moon: '<path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
};
function ic(nome, tam = 18) {
  const s = document.createElement('span');
  s.innerHTML = `<svg width="${tam}" height="${tam}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${IC[nome]}</svg>`;
  return s.firstChild;
}
// Marcadores data-ic viram ícones: dentro do elemento, se for <i>; no lugar dele, se for <span>.
for (const alvo of document.querySelectorAll('[data-ic]')) {
  const icone = ic(alvo.dataset.ic, Number(alvo.dataset.tam) || 18);
  if (alvo.tagName === 'I') alvo.replaceChildren(icone); else alvo.replaceWith(icone);
}

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
const CORES = ['#0d47a1', '#1976d2', '#2e9e5b', '#0f9d76', '#0c8fd0', '#2e416d', '#4f6fcf', '#0f766e'];
const GRUPOS = ['Bancos e promotoras', 'Comunicação', 'Pontual e ferramentas'];
const FUNCOES = { superadmin: 'Superadministrador', admin: 'Administrador', support: 'Suporte', auditor: 'Auditor' };
const FUNCAO_DICA = {
  superadmin: 'Faz tudo, inclusive criar outros administradores.',
  admin: 'Gerencia máquinas, sites, programas, manutenção e alertas.',
  support: 'Acompanha as máquinas e resolve alertas. Não altera sites nem programas.',
  auditor: 'Só consulta: máquinas, alertas e o registro de auditoria.',
};
const ESTADOS = {
  // cor: fundo da etiqueta no desenho do escritório (texto branco por cima; igual nos dois temas).
  // tinta e fundo: ícone dos cartões de números; são variáveis para acompanhar o modo escuro.
  online: { rotulo: 'Online', selo: 'online', ic: 'power', cor: '#15803d', tinta: 'var(--verde-700)', fundo: 'var(--verde-100)', sala: 'online' },
  alert: { rotulo: 'Com alerta', selo: 'alerta', ic: 'alert', cor: '#b45309', tinta: 'var(--ambar-700)', fundo: 'var(--ambar-100)', sala: 'alerta' },
  offline: { rotulo: 'Offline', selo: 'offline', ic: 'ban', cor: '#64748b', tinta: 'var(--suave)', fundo: 'var(--cinza-100)', sala: 'offline' },
  maintenance: { rotulo: 'Em manutenção', selo: 'manutencao', ic: 'wrench', cor: '#3730a3', tinta: 'var(--indigo-700)', fundo: 'var(--indigo-100)', sala: 'manutencao' },
  pending: { rotulo: 'Aguardando aprovação', selo: 'pendente', ic: 'clock', cor: '#b45309', tinta: 'var(--ambar-700)', fundo: 'var(--ambar-100)', sala: 'offline' },
};
const estadoDe = (d) => ESTADOS[d.state] || ESTADOS.offline;

const texto = (v) => (v === null || v === undefined ? '' : String(v));
const somaHash = (s) => { let h = 0; for (const c of texto(s)) h = (h * 31 + c.charCodeAt(0)) >>> 0; return h; };
const cor = (nome) => CORES[somaHash(nome) % CORES.length];
const iniciais = (nome) => { const p = texto(nome).trim().split(/\s+/).filter(Boolean); if (!p.length) return '?'; return (p[0][0] + (p[1] ? p[1][0] : p[0][1] || '')).toUpperCase(); };
const avatar = (nome, classe = '') => el('span', { className: `avatar ${classe}`, textContent: iniciais(nome), style: `background:${cor(nome)}`, 'aria-hidden': 'true' });
// "Todas as atendentes" vai em minúsculas no meio de uma frase; nome de pessoa, não.
const noMeio = (frase) => (/atendente/i.test(frase) ? frase.toLowerCase() : frase);
const selo = (classe, rotulo) => el('span', { className: `selo s-${classe}`, textContent: rotulo });
const primeiro = (nome) => texto(nome).trim().split(/\s+/)[0] || '';
const nomeDe = (d) => texto(d.attendantName).trim() || texto(d.hostname) || 'Máquina sem nome';
const saudar = () => { const h = new Date().getHours(); return h < 12 ? 'Bom dia' : h < 18 ? 'Boa tarde' : 'Boa noite'; };
const plural = (n, um, varios) => `${n} ${n === 1 ? um : varios}`;

const hhmm = (d) => d.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
// O contrato manda datas em UTC; se vier uma sem fuso, é lida como UTC, e não como hora local.
function dataDe(iso) {
  if (!iso) return null;
  const d = new Date(typeof iso === 'string' && /^d{4}-dd-ddT[d:.]+$/.test(iso) ? `${iso}Z` : iso);
  return Number.isNaN(d.getTime()) ? null : d;
}
function mesmoDia(a, b) { return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }
function quando(iso) {
  const d = dataDe(iso);
  if (!d) return 'Sem data';
  const hoje = new Date(), ontem = new Date(hoje.getTime() - 86400000);
  if (mesmoDia(d, hoje)) return `Hoje, ${hhmm(d)}`;
  if (mesmoDia(d, ontem)) return `Ontem, ${hhmm(d)}`;
  return `${d.toLocaleDateString('pt-BR')}, ${hhmm(d)}`;
}
function quandoCurto(iso) {
  const d = dataDe(iso);
  if (!d) return '';
  return mesmoDia(d, new Date()) ? hhmm(d) : `${d.toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit' })} ${hhmm(d)}`;
}
function haQuanto(iso) {
  const d = dataDe(iso);
  if (!d) return 'nunca';
  const s = Math.max(1, Math.round((Date.now() - d.getTime()) / 1000));
  if (s < 60) return `há ${s} s`;
  if (s < 3600) return `há ${Math.floor(s / 60)} min`;
  if (s < 86400) return `há ${Math.floor(s / 3600)} h`;
  return `há ${plural(Math.floor(s / 86400), 'dia', 'dias')}`;
}
function duracao(ms) {
  const min = Math.max(0, Math.floor(ms / 60000));
  return min < 60 ? `${min} min` : `${Math.floor(min / 60)} h ${String(min % 60).padStart(2, '0')} min`;
}

// Troca os filhos de um elemento e devolve o foco a quem o tinha (pela marca data-k),
// para a atualização periódica não atrapalhar quem navega pelo teclado.
function comFoco(alvo, fazer) {
  const ativo = document.activeElement;
  const chave = ativo && ativo !== document.body && alvo.contains(ativo) ? ativo.dataset.k : null;
  let ini = null, fim = null;
  if (chave && typeof ativo.selectionStart === 'number') { ini = ativo.selectionStart; fim = ativo.selectionEnd; }
  fazer();
  if (!chave) return;
  const novo = [...alvo.querySelectorAll('[data-k]')].find((n) => n.dataset.k === chave);
  if (!novo || novo.disabled) return;
  novo.focus({ preventScroll: true });
  if (ini !== null) { try { novo.setSelectionRange(ini, fim); } catch { /* campo sem seleção */ } }
}
const trocar = (alvo, filhos) => comFoco(alvo, () => alvo.replaceChildren(...filhos.filter(Boolean)));

// ================================================================== estado
const S = {};
function zerarEstado() {
  Object.assign(S, {
    me: null, secao: 'visao', visao: 'lista', sel: null, aba: 'resumo', fTipo: 'Todos',
    devices: null, alerts: null, notifs: null, items: null, observed: null, chrome: null, aplicativos: null, audit: null, admins: null,
    erro: {}, mut: 0, hist: { id: null, linhas: null, erro: null },
    rascunho: new Map(), erroLinha: new Map(), novas: new Set(), notifInicial: true,
    verificacao: null, assin: {}, pendente: false, focoAntes: null, marcando: false,
  });
}
zerarEstado();
const pode = (codigo) => Boolean(S.me && Array.isArray(S.me.permissions) && S.me.permissions.includes(codigo));
const aprovadas = () => (S.devices || []).filter((d) => d.state !== 'pending');
const maquina = (id) => (S.devices || []).find((d) => d.id === id) || null;
// Só redesenha uma área quando os dados dela mudaram.
function mudou(chave, valor) {
  const assinatura = JSON.stringify(valor);
  if (S.assin[chave] === assinatura) return false;
  S.assin[chave] = assinatura;
  return true;
}

// ===================================================================== API
class ErroApi extends Error {
  constructor(status, mensagem) { super(mensagem); this.status = status; }
}
const ERRO_PADRAO = {
  400: 'O servidor recusou o pedido. Confira os dados e tente de novo.',
  401: 'Sua sessão terminou. Entre de novo.',
  403: 'A sua função não permite fazer isso.',
  404: 'Não encontrado. O item pode ter sido removido por outra pessoa.',
  409: 'Não foi possível concluir: a situação mudou. Atualize e tente de novo.',
  413: 'Os dados enviados são grandes demais.',
  429: 'Muitas tentativas em pouco tempo. Aguarde um minuto e tente de novo.',
  500: 'O servidor encontrou um erro. Tente de novo em instantes.',
};
function marcarConexao(ok) {
  $('faixa-rede').hidden = ok;
}
async function api(metodo, caminho, corpo) {
  const opcoes = { method: metodo, credentials: 'same-origin', headers: { Accept: 'application/json' } };
  if (metodo !== 'GET') opcoes.headers['X-Pontual'] = '1';
  if (corpo !== undefined) { opcoes.headers['Content-Type'] = 'application/json'; opcoes.body = JSON.stringify(corpo); }
  let resposta;
  try {
    resposta = await fetch(`/api/v1${caminho}`, opcoes);
  } catch {
    marcarConexao(false);
    throw new ErroApi(0, 'Sem conexão com o servidor. Tente de novo em instantes.');
  }
  marcarConexao(true);
  let dados = null;
  if (resposta.status !== 204) {
    const bruto = await resposta.text().catch(() => '');
    if (bruto) { try { dados = JSON.parse(bruto); } catch { dados = null; } }
  }
  if (resposta.ok) return dados;
  const mensagem = (dados && typeof dados.error === 'string' && dados.error.trim())
    || ERRO_PADRAO[resposta.status] || `O servidor respondeu com o erro ${resposta.status}.`;
  // Qualquer 401 depois de entrar significa que a sessão acabou: volta ao login.
  if (resposta.status === 401 && S.me && caminho !== '/auth/login') sessaoPerdida();
  throw new ErroApi(resposta.status, mensagem);
}

const FONTES = {
  devices: ['/devices', 'devices.read'], alerts: ['/alerts', 'alerts.read'], notifs: ['/notifications', 'devices.read'],
  items: ['/items', 'apps.read'], observed: ['/observed-hosts', 'apps.read'], chrome: ['/chrome-sites', 'apps.read'], aplicativos: ['/device-apps', 'apps.read'],
  audit: ['/audit', 'audit.read'], admins: ['/admins', 'admins.manage'],
  // A seção Assistente carrega os próprios dados (assistente.js); esta linha só diz qual permissão a abre.
  assistente: ['/assistant', 'config.read'],
};
// Busca as listas pedidas. Se o administrador alterou algo enquanto a busca corria,
// o resultado é descartado para não desfazer na tela o que ele acabou de fazer.
async function carregar(...nomes) {
  const versao = S.mut, eu = S.me;
  const permitidos = nomes.filter((n) => pode(FONTES[n][1]));
  const respostas = await Promise.allSettled(permitidos.map((n) => api('GET', FONTES[n][0])));
  if (S.me !== eu || versao !== S.mut) return;
  respostas.forEach((r, i) => {
    const nome = permitidos[i];
    if (r.status === 'fulfilled') {
      S[nome] = Array.isArray(r.value) ? r.value : [];
      S.erro[nome] = null;
      if (nome === 'notifs') conferirUrgentes();
    } else if (r.reason.status !== 401) {
      S.erro[nome] = r.reason.message;
    }
  });
}
async function carregarHistorico(id) {
  if (!pode('usage.read')) return;
  if (S.hist.id !== id) S.hist = { id, linhas: null, erro: null };
  try {
    const linhas = await api('GET', `/devices/${encodeURIComponent(id)}/history`);
    if (S.hist.id === id) { S.hist.linhas = Array.isArray(linhas) ? linhas : []; S.hist.erro = null; }
  } catch (e) {
    if (S.hist.id === id && e.status !== 401) S.hist.erro = e.message;
  }
}

// Executa uma ação do administrador: bloqueia o botão, recarrega o que mudou,
// redesenha e mostra a confirmação (ou o erro do servidor) na linha de recado.
async function agir(botao, fazer, { recarregar = [], ok = '' } = {}) {
  if (botao) botao.disabled = true;
  try {
    await fazer();
    S.mut += 1;
    if (recarregar.length) await carregar(...recarregar);
    desenhar();
    if (ok) recado(ok);
    return true;
  } catch (e) {
    if (e.status !== 401) recado(e.message, true);
    return false;
  } finally {
    if (botao && botao.isConnected) botao.disabled = false;
  }
}

// ======================================================== telas de entrada
function mostrarTela(nome) {
  for (const id of ['carga', 'rede', 'setup', 'login', 'conta']) $(`tela-${id}`).hidden = id !== nome;
  $('app-ad').hidden = nome !== 'app';
  // O botão do tema solto no canto é só das telas de entrada; na Central ele fica no cabeçalho.
  $('tema-entrada').hidden = nome === 'app';
}
let novaTentativa = null;
async function iniciar() {
  clearTimeout(novaTentativa);
  try {
    const situacao = await api('GET', '/setup/status');
    if (situacao && situacao.needsSetup) { mostrarTela('setup'); $('setup-nome').focus(); return; }
    let eu = null;
    try { eu = await api('GET', '/auth/me'); } catch (e) { if (e.status !== 401) throw e; }
    if (!eu) { mostrarLogin(); return; }
    await entrar(eu);
  } catch (e) {
    $('rede-texto').textContent = e.status === 0
      ? 'Não foi possível falar com o servidor do Terminal Pontual. Uma nova tentativa é feita sozinha a cada poucos segundos.'
      : `${e.message} Uma nova tentativa é feita sozinha a cada poucos segundos.`;
    mostrarTela('rede');
    novaTentativa = setTimeout(iniciar, 4000);
  }
}
$('rede-tentar').onclick = () => { mostrarTela('carga'); iniciar(); };

function mostrarLogin(aviso = '') {
  $('login-recado').textContent = aviso; $('login-recado').hidden = !aviso;
  $('login-erro').hidden = true; $('login-senha').value = '';
  mostrarTela('login');
  ($('login-email').value ? $('login-senha') : $('login-email')).focus();
}
function falharEm(id, mensagem) { $(id).textContent = mensagem; $(id).hidden = false; }

$('form-setup').addEventListener('submit', async (e) => {
  e.preventDefault();
  const nome = $('setup-nome').value.trim(), email = $('setup-email').value.trim(), senha = $('setup-senha').value;
  $('setup-erro').hidden = true;
  if (!nome) return falharEm('setup-erro', 'Informe o seu nome.');
  if (!/^\S+@\S+\.\S+$/.test(email)) return falharEm('setup-erro', 'Informe um e-mail válido.');
  if (senha.length < 10) return falharEm('setup-erro', 'A senha precisa ter no mínimo 10 caracteres.');
  if (senha !== $('setup-senha2').value) return falharEm('setup-erro', 'As duas senhas não são iguais.');
  const botao = e.target.querySelector('[type="submit"]');
  botao.disabled = true;
  try {
    await api('POST', '/setup', { name: nome, email, password: senha });
    $('setup-senha').value = ''; $('setup-senha2').value = '';
    try {
      await api('POST', '/auth/login', { email, password: senha });
      await entrar(await api('GET', '/auth/me'));
    } catch {
      $('login-email').value = email;
      mostrarLogin('Administrador criado. Entre com o e-mail e a senha que você acabou de definir.');
    }
  } catch (erro) {
    // 409: outra pessoa já criou o primeiro administrador.
    if (erro.status === 409) mostrarLogin(erro.message); else falharEm('setup-erro', erro.message);
  } finally { botao.disabled = false; }
});

$('form-login').addEventListener('submit', async (e) => {
  e.preventDefault();
  const email = $('login-email').value.trim(), senha = $('login-senha').value;
  $('login-erro').hidden = true; $('login-recado').hidden = true;
  if (!email || !senha) return falharEm('login-erro', 'Informe o e-mail e a senha.');
  const botao = e.target.querySelector('[type="submit"]');
  botao.disabled = true;
  try {
    await api('POST', '/auth/login', { email, password: senha });
    $('login-senha').value = '';
    await entrar(await api('GET', '/auth/me'));
  } catch (erro) {
    falharEm('login-erro', erro.status === 401 && erro.message === ERRO_PADRAO[401] ? 'E-mail ou senha incorretos.' : erro.message);
    $('login-senha').select();
  } finally { botao.disabled = false; }
});

// Adicionar conta pela tela de login: um superadministrador autoriza com o e-mail e a senha dele.
const CAMPOS_CONTA = ['conta-nome', 'conta-email', 'conta-senha', 'conta-senha2', 'conta-aut-email', 'conta-aut-senha'];
function dicaTipoConta() { $('conta-tipo-dica').textContent = FUNCAO_DICA[$('conta-tipo').value] || ''; }
$('conta-tipo').onchange = dicaTipoConta;
$('login-nova-conta').onclick = () => {
  for (const id of CAMPOS_CONTA) $(id).value = '';
  $('conta-tipo').value = 'admin'; dicaTipoConta();
  $('conta-erro').hidden = true;
  mostrarTela('conta');
  $('conta-nome').focus();
};
$('conta-voltar').onclick = () => { for (const id of CAMPOS_CONTA) $(id).value = ''; mostrarLogin(); };

$('form-conta').addEventListener('submit', async (e) => {
  e.preventDefault();
  const nome = $('conta-nome').value.trim(), email = $('conta-email').value.trim(), senha = $('conta-senha').value;
  const autEmail = $('conta-aut-email').value.trim(), autSenha = $('conta-aut-senha').value;
  $('conta-erro').hidden = true;
  if (!nome) return falharEm('conta-erro', 'Informe o nome de quem vai usar a conta.');
  if (!/^\S+@\S+\.\S+$/.test(email)) return falharEm('conta-erro', 'Informe um e-mail válido para a conta nova.');
  if (senha.length < 10) return falharEm('conta-erro', 'A senha precisa ter no mínimo 10 caracteres.');
  if (senha !== $('conta-senha2').value) return falharEm('conta-erro', 'As duas senhas não são iguais.');
  if (!autEmail || !autSenha) return falharEm('conta-erro', 'Informe o e-mail e a senha de um superadmin para autorizar.');
  const botao = e.target.querySelector('[type="submit"]');
  botao.disabled = true;
  try {
    await api('POST', '/auth/register', { name: nome, email, password: senha, role: $('conta-tipo').value, authEmail: autEmail, authPassword: autSenha });
    for (const id of CAMPOS_CONTA) $(id).value = '';
    $('login-email').value = email;
    mostrarLogin('Conta criada. Entre com o e-mail e a senha definidos.');
  } catch (erro) {
    falharEm('conta-erro', erro.message);
    if (erro.status === 401 || erro.status === 403) $('conta-aut-senha').select();
  } finally { botao.disabled = false; }
});

async function entrar(eu) {
  zerarEstado();
  S.me = eu;
  $('avisos').replaceChildren();
  esconderRecado();
  $('eu-avatar').textContent = iniciais(eu.name); $('eu-avatar').style.background = cor(eu.name);
  $('eu-nome').textContent = primeiro(eu.name) || eu.email;
  $('eu-funcao').textContent = eu.role === 'superadmin' ? 'Superadmin' : FUNCOES[eu.role] || texto(eu.role);
  $('eu').setAttribute('aria-label', `${texto(eu.name)}, ${FUNCOES[eu.role] || texto(eu.role)}. Abrir o menu da conta`);
  for (const b of document.querySelectorAll('.menu [data-secao]')) b.hidden = !pode(FONTES[SECOES[b.dataset.secao]][1]);
  $('sino').hidden = !pode('devices.read');
  $('ir-admins').hidden = !pode('admins.manage');
  $('nova-maquina').hidden = !pode('enrollment.create');
  $('novo-site').hidden = !pode('apps.websites.manage');
  $('novo-prog').hidden = !pode('apps.executables.propose');
  $('f-app').value = '';
  mostrarTela('app');
  irSecao('visao');
  await carregar('devices', 'alerts', 'notifs');
  desenhar();
  iniciarCiclo();
}
function encerrarLocal() {
  pararCiclo();
  for (const d of document.querySelectorAll('dialog[open]')) d.close();
  $('gaveta').hidden = true; $('gaveta-veu').hidden = true; fecharMenuEu();
  $('avisos').replaceChildren();
  zerarEstado();
}
function sessaoPerdida() {
  if (!S.me) return;
  encerrarLocal();
  mostrarLogin('Sua sessão terminou. Entre de novo.');
}
async function sair() {
  try { await api('POST', '/auth/logout'); } catch (e) { if (e.status === 0) { recado('Sem conexão com o servidor. Não foi possível sair agora.', true); return; } }
  if (!S.me) return;
  encerrarLocal();
  mostrarLogin('Você saiu da Central do TI.');
}

// ======================================================== ciclo de consulta
let cicloAtual = 0, relogioCiclo = null, volta = 0;
function pararCiclo() { cicloAtual += 1; clearTimeout(relogioCiclo); }
function iniciarCiclo() {
  pararCiclo();
  const meu = cicloAtual;
  const passo = async () => {
    if (meu !== cicloAtual || !S.me) return;
    volta += 1;
    await atualizar(volta % 3 === 0);
    if (meu === cicloAtual && S.me) relogioCiclo = setTimeout(passo, 4000);
  };
  relogioCiclo = setTimeout(passo, 4000);
}
// Máquinas, alertas, notificações, sites nos navegadores e aplicativos a cada 4 s. O resto, só o que está na tela, a cada 12 s.
async function atualizar(lento) {
  const nomes = ['devices', 'alerts', 'notifs', 'chrome', 'aplicativos'];
  if (lento) {
    if (S.secao === 'apps' || (S.sel && S.aba === 'acesso')) nomes.push('items', 'observed');
    if (S.secao === 'auditoria') nomes.push('audit');
    if (S.secao === 'admins') nomes.push('admins');
  }
  const tarefas = [carregar(...nomes)];
  if (S.sel && S.aba === 'historico') tarefas.push(carregarHistorico(S.sel));
  await Promise.all(tarefas);
  if (!S.me) return;
  if (S.secao === 'notificacoes') marcarLidas();
  if (document.querySelector('dialog[open]')) S.pendente = true; else desenhar();
}

// ============================================== recado (linha dentro da tela)
let relogioRecado = null;
function esconderRecado() { clearTimeout(relogioRecado); $('recado').hidden = true; $('gaveta-recado').hidden = true; }
function recado(mensagem, falhou = false) {
  esconderRecado();
  if (S.sel) {
    const alvo = $('gaveta-recado');
    alvo.replaceChildren(ic(falhou ? 'alert' : 'check', 18), el('span', { textContent: mensagem }));
    alvo.classList.toggle('falhou', falhou); alvo.hidden = false;
  } else {
    $('recado-icone').replaceChildren(ic(falhou ? 'alert' : 'check', 18));
    $('recado-texto').textContent = mensagem;
    $('recado').classList.toggle('falhou', falhou); $('recado').hidden = false;
  }
  relogioRecado = setTimeout(esconderRecado, falhou ? 12000 : 6000);
}
$('recado-fechar').onclick = esconderRecado;

// ============================================== avisos que saltam (urgentes)
// Guarda no navegador quais notificações urgentes já saltaram, para não repetir ao recarregar.
const CHAVE_VISTAS = 'pontual.central.urgentes-vistas';
function lerVistas() { try { return new Set(JSON.parse(localStorage.getItem(CHAVE_VISTAS) || '[]')); } catch { return new Set(); } }
function gravarVistas(vistas) { try { localStorage.setItem(CHAVE_VISTAS, JSON.stringify([...vistas].slice(-300))); } catch { /* sem armazenamento: só não lembra */ } }
function conferirUrgentes() {
  const vistas = lerVistas();
  const novas = (S.notifs || []).filter((n) => n.urgent === true && !n.read && !vistas.has(n.id));
  if (novas.length) { for (const n of novas) vistas.add(n.id); gravarVistas(vistas); }
  // Ao entrar, no máximo as três mais recentes; depois, cada uma que chegar.
  const mostrar = S.notifInicial ? novas.slice(0, 3) : novas;
  S.notifInicial = false;
  for (const n of mostrar.reverse()) saltar(n);
}
// Um diálogo aberto deixa o resto da página inerte e escurecido. Para o aviso urgente
// continuar visível e clicável, a caixa de avisos acompanha o diálogo que estiver aberto.
function lugarDosAvisos() {
  const abertos = document.querySelectorAll('dialog[open]');
  const lugar = abertos.length ? abertos[abertos.length - 1] : document.body;
  if ($('avisos').parentNode !== lugar) lugar.append($('avisos'));
}
for (const d of document.querySelectorAll('dialog')) new MutationObserver(lugarDosAvisos).observe(d, { attributes: true, attributeFilter: ['open'] });
function saltar(n) {
  const ver = el('button', { type: 'button', textContent: 'Ver alertas' });
  const fechar = el('button', { type: 'button', className: 'fechar-toast', textContent: 'Dispensar' });
  const caixa = el('div', { className: 'toast urgente', role: 'alert' }, ic('alert', 20),
    el('div', {}, el('span', { textContent: texto(n.text) }), el('div', {}, pode('alerts.read') ? ver : null, fechar)));
  ver.onclick = () => { caixa.remove(); for (const d of document.querySelectorAll('dialog[open]')) d.close(); fecharGaveta(); irSecao('alertas'); };
  fechar.onclick = () => caixa.remove();
  lugarDosAvisos();
  $('avisos').prepend(caixa);
  while ($('avisos').children.length > 4) $('avisos').lastChild.remove();
  setTimeout(() => caixa.remove(), 9000);
}
async function marcarLidas() {
  if (S.marcando || !S.notifs) return;
  const naoLidas = S.notifs.filter((n) => !n.read);
  if (!naoLidas.length) return;
  for (const n of naoLidas) S.novas.add(n.id);   // continuam marcadas como "Nova" enquanto a tela está aberta
  S.marcando = true;
  try {
    await api('POST', '/notifications/read');
    for (const n of S.notifs || []) n.read = true;
  } catch { /* tenta de novo na próxima consulta */ } finally { S.marcando = false; }
  if (S.me) { desenharTopo(); if (S.secao === 'notificacoes') desenharNotificacoes(); }
}

// ============================================================== navegação
const SECOES = { visao: 'devices', apps: 'items', chrome: 'chrome', aplicativos: 'aplicativos', alertas: 'alerts', notificacoes: 'notifs', assistente: 'assistente', auditoria: 'audit', admins: 'admins' };
function irSecao(secao) {
  if (!pode(FONTES[SECOES[secao]][1])) secao = 'visao';
  if (S.secao === 'notificacoes' && secao !== 'notificacoes') S.novas.clear();
  S.secao = secao;
  S.assin = {};
  for (const b of document.querySelectorAll('.menu [data-secao]')) { if (b.dataset.secao === secao) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); }
  for (const p of document.querySelectorAll('[data-painel]')) p.hidden = p.dataset.painel !== secao;
  esconderRecado();
  desenhar();
  window.scrollTo(0, 0);
  const extras = { apps: ['items', 'observed'], chrome: ['chrome'], aplicativos: ['aplicativos'], auditoria: ['audit'], admins: ['admins'] }[secao];
  if (extras) carregar(...extras).then(() => { if (S.secao === secao) desenhar(); });
  if (secao === 'notificacoes') marcarLidas();
  if (secao === 'assistente') Assistente.abrir();
}
for (const b of document.querySelectorAll('.menu [data-secao]')) b.onclick = () => irSecao(b.dataset.secao);
$('sino').onclick = () => irSecao('notificacoes');

// Modo escuro: a escolha fica gravada neste navegador (ver tema.js).
function desenharBotoesTema() {
  const escuro = Tema.atual() === 'escuro';
  for (const b of [$('tema'), $('tema-entrada')]) {
    b.replaceChildren(ic(escuro ? 'sun' : 'moon', 20));
    b.setAttribute('aria-label', escuro ? 'Ativar o modo claro' : 'Ativar o modo escuro');
    b.title = escuro ? 'Modo claro' : 'Modo escuro';
  }
}
for (const b of [$('tema'), $('tema-entrada')]) b.onclick = () => { Tema.trocar(); desenharBotoesTema(); };
desenharBotoesTema();
for (const b of document.querySelectorAll('[data-visao]')) b.onclick = () => { S.visao = b.dataset.visao; desenharVisao(); };

function fecharMenuEu() { $('menu-eu').hidden = true; $('eu').setAttribute('aria-expanded', 'false'); }
$('eu').onclick = () => {
  const abrir = $('menu-eu').hidden;
  $('menu-eu').hidden = !abrir; $('eu').setAttribute('aria-expanded', String(abrir));
  if (abrir) { const primeiroItem = $('menu-eu').querySelector('button:not([hidden])'); if (primeiroItem) primeiroItem.focus(); }
};
$('ir-admins').onclick = () => { fecharMenuEu(); irSecao('admins'); };
$('sair').onclick = () => { fecharMenuEu(); sair(); };
document.addEventListener('click', (e) => { if (!$('menu-eu').hidden && !e.target.closest('.eu-caixa')) fecharMenuEu(); });

document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  if (document.querySelector('dialog[open]')) return;   // o próprio diálogo fecha com Esc
  if (!$('menu-eu').hidden) { fecharMenuEu(); $('eu').focus(); return; }
  if (S.sel) fecharGaveta();
});
for (const d of document.querySelectorAll('dialog')) {
  for (const b of d.querySelectorAll('[data-fechar]')) b.onclick = () => d.close();
  // Clique fora do diálogo fecha, como no painel lateral.
  d.addEventListener('click', (e) => { if (e.target === d) d.close(); });
  // O que chegou do servidor enquanto o formulário estava aberto é desenhado agora.
  d.addEventListener('close', () => { if (S.me && S.pendente) { S.pendente = false; desenhar(); } });
}

// ============================================================ logo
for (const alvo of document.querySelectorAll('[data-logo]')) {
  const img = el('img', { alt: 'Pontual Consig' });
  // Sem internet, a logo é trocada pelo nome escrito.
  img.onerror = () => alvo.replaceChildren(el('span', { className: 'logo-texto' }, 'Pontual', el('em', { textContent: 'Consig' })));
  img.src = 'https://www.pontualconsig.com.br/frontend/assets/img/LOGO%20PONTUAL%20CONSIG%203.svg';
  alvo.append(img);
}

// ================================================================ desenho
function desenhar() {
  if (!S.me) return;
  desenharTopo();
  if (S.secao === 'visao') { desenharVisao(); desenharPendentes(); }
  if (S.secao === 'apps') { desenharPropostas(); desenharApps(); desenharObservados(); }
  if (S.secao === 'chrome') desenharChrome();
  if (S.secao === 'aplicativos') desenharAplicativos();
  if (S.secao === 'alertas') desenharAlertas();
  if (S.secao === 'notificacoes') desenharNotificacoes();
  if (S.secao === 'auditoria') desenharAuditoria();
  if (S.secao === 'admins') desenharAdmins();
  if (S.secao === 'assistente') Assistente.desenhar();
  desenharGaveta();
}
// Linha única para lista ainda carregando ou que falhou; nulo quando há dados.
function situacaoLista(nome) {
  if (S[nome]) return null;
  return S.erro[nome] ? `Não foi possível carregar. ${S.erro[nome]}` : 'Carregando…';
}

function desenharTopo() {
  const abertos = (S.alerts || []).length;
  $('conta-alertas').hidden = abertos === 0; $('conta-alertas').textContent = abertos;
  const esperando = (S.chrome || []).filter((c) => c.status === 'pending').length;
  $('conta-chrome').hidden = esperando === 0; $('conta-chrome').textContent = esperando;
  const appsEsperando = (S.aplicativos || []).filter((a) => a.status === 'pending').length;
  $('conta-aplicativos').hidden = appsEsperando === 0; $('conta-aplicativos').textContent = appsEsperando;
  const naoLidas = (S.notifs || []).filter((n) => !n.read).length;
  $('conta-notif').hidden = naoLidas === 0; $('conta-notif').textContent = naoLidas; $('sino-ponto').hidden = naoLidas === 0;
  $('sino').setAttribute('aria-label', naoLidas ? `Ver notificações: ${plural(naoLidas, 'nova', 'novas')}` : 'Ver notificações');
}

// ------------------------------------------------------------ visão geral
const USO_VAZIO = { offline: 'Desligado ou sem rede', maintenance: 'Fora de serviço' };
function desenharVisao() {
  const lista = aprovadas(), pendentes = (S.devices || []).length - lista.length;
  const n = {};
  for (const d of lista) { const e = ESTADOS[d.state] ? d.state : 'offline'; n[e] = (n[e] || 0) + 1; }
  const emUso = lista.filter((d) => (d.appsInUse || []).length > 0 && d.state !== 'offline').length;
  const atencao = (n.alert || 0) + (n.offline || 0);

  $('ad-saudacao').textContent = `${saudar()}, ${primeiro(S.me.name) || 'TI'}.`;
  const espera = situacaoLista('devices');
  if (espera) $('ad-frase').textContent = espera;
  else if (!lista.length) $('ad-frase').textContent = pendentes ? 'Nenhuma máquina aprovada ainda. Há máquinas aguardando a sua aprovação logo abaixo.' : 'Nenhuma máquina cadastrada ainda. Use "Cadastrar nova máquina" para começar.';
  else $('ad-frase').replaceChildren(el('strong', { textContent: emUso ? `${emUso} de ${plural(lista.length, 'computador', 'computadores')}` : 'Nenhum computador' }), emUso > 1 ? ' estão em uso agora. ' : ' está em uso agora. ',
    atencao ? el('strong', { textContent: `${atencao} ${atencao === 1 ? 'precisa' : 'precisam'} da sua atenção.` }) : 'Tudo em ordem.');

  const cartoes = ['online', 'alert', 'offline', 'maintenance'].filter((e) => n[e] || e === 'online' || e === 'alert').map((e) => [e, n[e] || 0]);
  if (pendentes) cartoes.push(['pending', pendentes]);
  if (mudou('numeros', cartoes)) $('numeros').replaceChildren(...cartoes.map(([e, total]) => {
    const i = el('i', { style: `background:${ESTADOS[e].fundo};color:${ESTADOS[e].tinta}` }, ic(ESTADOS[e].ic, 22));
    return el('div', { className: 'numero' }, i, el('div', {}, el('b', { textContent: total }), el('span', { textContent: e === 'pending' ? 'Aguardando' : ESTADOS[e].rotulo })));
  }));

  for (const b of document.querySelectorAll('[data-visao]')) b.setAttribute('aria-pressed', String(b.dataset.visao === S.visao));
  $('linhas').hidden = S.visao !== 'lista';
  $('sala-area').hidden = S.visao !== 'sala';

  if (S.visao === 'lista') {
    const vazio = espera || (!lista.length ? 'Nenhuma atendente por aqui ainda. Cadastre a primeira máquina para ela aparecer nesta lista.' : null);
    trocar($('linhas'), vazio ? [el('li', { className: 'vazio', textContent: vazio })] : lista.map((d) => {
      const e = estadoDe(d), apps = d.appsInUse || [];
      const b = el('button', { type: 'button', className: 'pessoa', 'data-k': `maq:${d.id}` }, avatar(nomeDe(d), 'g'),
        el('span', {}, el('b', { textContent: nomeDe(d) }), el('small', { textContent: [d.hostname, d.windows, d.terminalVersion ? `Terminal ${d.terminalVersion}` : ''].filter(Boolean).join(', ') })),
        el('span', { className: 'col-2' }, selo(e.selo, e.rotulo)),
        el('span', { className: 'uso', textContent: apps.length ? apps.join(', ') : USO_VAZIO[d.state] || 'Nenhum aplicativo aberto' }),
        el('small', { className: 'col-4 num', textContent: haQuanto(d.lastSeenAt) }), ic('chevron', 20));
      b.setAttribute('aria-label', `${nomeDe(d)}, ${e.rotulo}. Ver detalhes`);
      b.onclick = () => abrirGaveta(d.id);
      return el('li', {}, b);
    }));
  } else {
    const mesas = mesasDaSala(), naSala = mesas.slice(0, Escritorio.MAXIMO), alvo = $('sala');
    // Botão de um aparelho: abre os detalhes dele. Na mesa com dois aparelhos, cada um ocupa metade.
    const botaoDe = (d, classe, etiqueta) => {
      const e = estadoDe(d), tipo = ehCelular(d) ? 'celular' : 'computador';
      const b = el('button', { type: 'button', className: classe, 'data-k': `mesa:${d.id}`, title: `${nomeDe(d)}: ${tipo}, ${e.rotulo}` },
        etiqueta ? null : el('b', { textContent: primeiro(nomeDe(d)) }), el('small', { textContent: etiqueta || e.rotulo, style: `background:${e.cor}` }));
      b.setAttribute('aria-label', `${nomeDe(d)}, ${tipo}, ${e.rotulo}. Ver detalhes`);
      b.onclick = () => abrirGaveta(d.id);
      return b;
    };
    comFoco(alvo, () => {
      for (const antigo of alvo.querySelectorAll('.mesa')) antigo.remove();
      naSala.forEach((m, i) => {
        const caixa = m.pc && m.celular
          ? el('div', { className: 'mesa dupla' }, botaoDe(m.pc, 'meia', 'PC'), botaoDe(m.celular, 'meia', 'Celular'), el('b', { textContent: primeiro(nomeDe(m.pc)) }))
          : botaoDe(m.pc || m.celular, 'mesa');
        Object.assign(caixa.style, Escritorio.areaDaMesa(i));
        alvo.append(caixa);
      });
    });
    const nota = espera || (!lista.length ? 'O escritório está vazio: nenhuma máquina aprovada ainda.'
      : mesas.length > Escritorio.MAXIMO ? `Mostrando as ${Escritorio.MAXIMO} primeiras de ${mesas.length} mesas. Use a Lista para ver todas.` : '');
    $('sala-nota').textContent = nota; $('sala-nota').hidden = !nota;
    pintarSala();
  }
}
// O celular se cadastra com o sistema "Android ..." (ver Core.systemLabel no aplicativo Android).
const ehCelular = (d) => /^android/i.test(texto(d.windows).trim());
// Mesas do escritório: uma por atendente. O computador e o celular com o mesmo nome de atendente
// dividem a mesa; aparelho sem nome de atendente, ou repetido do mesmo tipo, ganha mesa própria.
function mesasDaSala() {
  const mesas = [], porNome = new Map();
  for (const d of aprovadas()) {
    const nome = texto(d.attendantName).trim().toLowerCase(), tipo = ehCelular(d) ? 'celular' : 'pc';
    const dela = nome ? porNome.get(nome) || porNome.set(nome, []).get(nome) : null;
    let mesa = dela ? dela.find((m) => !m[tipo]) : null;
    if (!mesa) { mesa = { pc: null, celular: null }; mesas.push(mesa); if (dela) dela.push(mesa); }
    mesa[tipo] = d;
  }
  return mesas;
}
let quadro = 0;
const semMovimento = window.matchMedia('(prefers-reduced-motion: reduce)');
function pintarSala() {
  const mesas = mesasDaSala().slice(0, Escritorio.MAXIMO).map((m) => ({
    pc: m.pc ? estadoDe(m.pc).sala : null, celular: m.celular ? estadoDe(m.celular).sala : null,
    visual: somaHash((m.pc || m.celular).id) % 24,
  }));
  Escritorio.desenhar($('canvas'), mesas, semMovimento.matches ? 0 : quadro);
}
setInterval(() => {
  if (!S.me || S.secao !== 'visao' || S.visao !== 'sala' || semMovimento.matches || document.hidden) return;
  quadro += 1; pintarSala();
}, 320);

function desenharPendentes() {
  const alvo = $('maquinas-pendentes');
  const pendentes = (S.devices || []).filter((d) => d.state === 'pending');
  const aprova = pode('devices.manage'), recusa = pode('devices.revoke');
  if (!mudou('pendentes', [pendentes.map((d) => [d.id, d.hostname, d.windows]), [...S.erroLinha], aprova, recusa])) return;
  if (!pendentes.length) { alvo.replaceChildren(); return; }
  trocar(alvo, [el('h2', { className: 'pendencias-titulo', textContent: `Máquinas aguardando aprovação (${pendentes.length})` }),
    ...pendentes.map((d) => {
      const chave = `aprovar:${d.id}`, idCampo = `nome-${d.id}`;
      const campo = el('input', { className: 'campo', id: idCampo, value: S.rascunho.get(chave) || '', maxLength: 80, placeholder: 'Ex.: Marina Alves', autocomplete: 'off', 'data-k': `campo:${d.id}` });
      campo.oninput = () => S.rascunho.set(chave, campo.value);
      const aprovar = el('button', { type: 'submit', className: 'btn verde', 'data-k': `ok:${d.id}` }, ic('check', 18), 'Aprovar');
      const recusar = el('button', { type: 'button', className: 'btn claro', textContent: 'Recusar', 'data-k': `nao:${d.id}` });
      const erro = S.erroLinha.get(chave);
      const form = el('form', { className: 'pendencia maquina', noValidate: true }, el('i', {}, ic('monitor', 24)),
        el('div', {}, el('h3', { textContent: texto(d.hostname) || 'Máquina sem nome' }),
          el('p', { textContent: `${texto(d.windows) || 'Windows não informado'}. Confira se é o computador certo antes de aprovar.` })),
        aprova ? el('label', { for: idCampo }, 'Nome da atendente', campo) : null,
        aprova ? aprovar : null, recusa ? recusar : null,
        !aprova ? el('p', { className: 'dica', textContent: 'A sua função não permite aprovar máquinas.' }) : null,
        erro ? el('p', { className: 'erro', role: 'alert', textContent: erro }) : null);
      form.onsubmit = async (ev) => {
        ev.preventDefault();
        const nome = campo.value.trim();
        if (!nome) { S.erroLinha.set(chave, 'Informe o nome da atendente que usa esta máquina.'); desenharPendentes(); $(idCampo)?.focus(); return; }
        aprovar.disabled = true;
        try {
          await api('POST', `/devices/${encodeURIComponent(d.id)}/approve`, { attendantName: nome });
          S.mut += 1; S.rascunho.delete(chave); S.erroLinha.delete(chave);
          await carregar('devices'); desenhar();
          recado(`${texto(d.hostname)} aprovada para ${nome}. Ela já pode usar o Terminal.`);
        } catch (e) {
          if (e.status === 401) return;
          S.erroLinha.set(chave, e.message); aprovar.disabled = false; desenharPendentes();
        }
      };
      recusar.onclick = async () => {
        const sim = await confirmar({ titulo: 'Recusar esta máquina?', texto: `${texto(d.hostname)} sai da lista e o código usado por ela deixa de valer. Para cadastrar de novo, será preciso gerar outro código.`, botao: 'Recusar máquina' });
        if (!sim) return;
        S.erroLinha.delete(chave); S.rascunho.delete(chave);
        await agir(null, () => api('DELETE', `/devices/${encodeURIComponent(d.id)}`), { recarregar: ['devices'], ok: `${texto(d.hostname)} foi recusada.` });
      };
      return form;
    })]);
}

// ------------------------------------------------- cadastrar nova máquina
let relogioCodigo = null, codigoVence = null;
function pintarValidade() {
  const alvo = $('codigo-validade');
  if (!codigoVence) { alvo.replaceChildren(); return; }
  const falta = codigoVence.getTime() - Date.now();
  alvo.classList.toggle('vencido', falta <= 0);
  if (falta <= 0) { alvo.replaceChildren(ic('alert', 16), 'Este código venceu. Gere outro.'); clearInterval(relogioCodigo); return; }
  const min = Math.floor(falta / 60000), seg = Math.floor((falta % 60000) / 1000);
  alvo.replaceChildren(ic('clock', 16), `Vale até ${hhmm(codigoVence)} (mais ${min > 0 ? `${min} min ` : ''}${String(seg).padStart(2, '0')} s). Só pode ser usado uma vez.`);
}
async function gerarCodigo(botao) {
  botao.disabled = true;
  $('codigo-erro').hidden = true;
  try {
    const r = await api('POST', '/enrollment-codes');
    S.mut += 1;
    $('codigo-valor').textContent = texto(r && r.code);
    codigoVence = dataDe(r && r.expiresAt);
    clearInterval(relogioCodigo); pintarValidade(); relogioCodigo = setInterval(pintarValidade, 1000);
    if (!$('dlg-codigo').open) $('dlg-codigo').showModal();
    return true;
  } catch (e) {
    if (e.status === 401) return false;
    if ($('dlg-codigo').open) falharEm('codigo-erro', e.message); else recado(e.message, true);
    return false;
  } finally { botao.disabled = false; }
}
$('nova-maquina').onclick = () => gerarCodigo($('nova-maquina'));
$('codigo-outro').onclick = () => gerarCodigo($('codigo-outro'));
$('dlg-codigo').addEventListener('close', () => { clearInterval(relogioCodigo); $('codigo-valor').textContent = ''; codigoVence = null; carregar('devices').then(desenhar); });

// ---------------------------------------------------------------- gaveta
// Visualização ao vivo da tela de uma máquina. Enquanto o diálogo está aberto, busca a imagem mais recente
// a cada pouco mais de um segundo; ao fechar, avisa o servidor, e o computador para de enviar.
// Nada é gravado: o servidor guarda só a última imagem, na memória, e a descarta quando a janela fecha.
async function abrirTela(d, nome) {
  const dlg = $('dlg-tela'), img = $('tela-img'), estado = $('tela-estado');
  const caminho = `/devices/${encodeURIComponent(d.id)}/screen`;
  const dizer = (mensagem) => { estado.textContent = mensagem; estado.hidden = false; img.hidden = true; };
  $('t-tela').textContent = `Tela de ${texto(nome) || 'atendente'}`;
  $('tela-sub').textContent = 'Ao vivo, sem gravação. A atendente vê o aviso "O TI está vendo sua tela" enquanto esta janela estiver aberta, e a abertura fica na auditoria.';
  img.removeAttribute('src');
  dizer('Pedindo a imagem ao computador. Leva alguns segundos.');
  dlg.showModal();

  let vivo = true, url = null, temImagem = false;
  const parar = () => {
    if (!vivo) return;
    vivo = false;
    api('DELETE', caminho).catch(() => {});
    if (url) URL.revokeObjectURL(url);
    img.removeAttribute('src');
  };
  dlg.addEventListener('close', parar, { once: true });

  // Troca a imagem só depois de o navegador terminar de decodificar a nova, para não piscar nem mostrar quadro pela metade.
  const trocar = (novoUrl) => new Promise((pronto) => {
    const prova = new Image();
    prova.onload = () => {
      if (!vivo) { URL.revokeObjectURL(novoUrl); pronto(); return; }
      img.src = novoUrl;
      if (url) URL.revokeObjectURL(url);
      url = novoUrl; temImagem = true;
      img.hidden = false; estado.hidden = true;
      pronto();
    };
    prova.onerror = () => { URL.revokeObjectURL(novoUrl); pronto(); };
    prova.src = novoUrl;
  });

  try {
    await api('POST', caminho);
  } catch (e) {
    dizer(e && e.message ? e.message : 'Não foi possível abrir a visualização.');
    vivo = false;
    return;
  }

  const inicio = Date.now();
  while (vivo) {
    let atraso = 450;
    try {
      const r = await fetch(`/api/v1${caminho}`, { credentials: 'same-origin', cache: 'no-store' });
      if (!vivo) break;
      if (r.status === 200) {
        await trocar(URL.createObjectURL(await r.blob()));
      } else if (r.status === 204) {
        // Ainda sem a primeira imagem: espera um pouco mais entre as tentativas.
        atraso = 900;
        if (!temImagem && Date.now() - inicio > 20000) dizer('O computador ainda não respondeu. Ele pode estar desligado, sem conexão ou com uma versão antiga do Terminal.');
      } else if (r.status === 401) {
        sessaoPerdida(); break;
      } else if (r.status === 410) {
        dizer('A visualização foi encerrada por ficar parada. Feche esta janela e clique em "Ver a tela" de novo.'); vivo = false; break;
      } else {
        dizer(ERRO_PADRAO[r.status] || `O servidor respondeu com o erro ${r.status}.`);
      }
    } catch {
      dizer('Sem conexão com o servidor. Tentando de novo.');
      atraso = 900;
    }
    if (vivo) await new Promise((ok) => setTimeout(ok, atraso));
  }
}

function abrirGaveta(id, aba = 'resumo') {
  const ativo = document.activeElement;
  S.focoAntes = ativo && ativo.dataset ? ativo.dataset.k || null : null;
  S.sel = id; S.aba = aba; delete S.assin['gaveta-topo'];
  esconderRecado();
  desenharGaveta();
  $('gaveta-corpo').scrollTop = 0;
  const fechar = $('gaveta-topo').querySelector('button');
  if (fechar) fechar.focus();
}
function fecharGaveta() {
  if (!S.sel) return;
  S.sel = null; $('gaveta').hidden = true; $('gaveta-veu').hidden = true; $('gaveta-recado').hidden = true;
  const chave = S.focoAntes; S.focoAntes = null;
  const volta2 = chave ? [...document.querySelectorAll('[data-k]')].find((n) => n.dataset.k === chave) : null;
  if (volta2) volta2.focus({ preventScroll: true });
}
$('gaveta-veu').onclick = fecharGaveta;
function mudarAba(aba) {
  S.aba = aba;
  if (aba === 'historico') carregarHistorico(S.sel).then(desenharGaveta);
  if (aba === 'acesso' && !S.items) carregar('items').then(desenharGaveta);
  desenharGaveta();
  $('gaveta-corpo').scrollTop = 0;
}
// Acesso por máquina: "audience" nulo significa todas; senão, a lista de ids que veem o item.
const temAcesso = (item, id) => !item.audience || item.audience.includes(id);
const podeEditar = (item) => (item.kind === 'program' ? pode('apps.executables.approve') : pode('apps.websites.manage'));
function novaAudiencia(item, id, ligado) {
  const todas = (S.devices || []).map((d) => d.id);
  const lista = new Set(item.audience || todas);
  if (ligado) lista.add(id); else lista.delete(id);
  return todas.every((t) => lista.has(t)) ? null : [...lista];
}
function quemUsa(item) {
  if (!item.audience) return 'Todas as atendentes';
  const nomes = item.audience.map(maquina).filter(Boolean).map((d) => primeiro(nomeDe(d)));
  if (nomes.length === 0) return 'Nenhuma atendente';
  return nomes.length <= 2 ? nomes.join(' e ') : `${nomes.length} atendentes`;
}

function desenharGaveta() {
  if (!S.sel) return;
  const d = maquina(S.sel);
  if (!d) { if (S.devices) fecharGaveta(); return; }
  const e = estadoDe(d), nome = nomeDe(d), apps = d.appsInUse || [];
  const guias = [['resumo', 'Resumo', true], ['acesso', 'Sites e programas', pode('apps.read')], ['historico', 'Histórico', pode('usage.read')]].filter((g) => g[2]);
  if (!guias.some((g) => g[0] === S.aba)) S.aba = 'resumo';

  if (mudou('gaveta-topo', [d.id, nome, d.hostname, d.state, S.aba, guias.length])) {
    const fechar = el('button', { type: 'button', className: 'redondo', 'aria-label': 'Fechar detalhes', 'data-k': 'gaveta-fechar' }, ic('x', 20));
    fechar.onclick = fecharGaveta;
    trocar($('gaveta-topo'), [avatar(nome, 'g'), el('div', {}, el('h2', { id: 'gaveta-nome', textContent: nome }), el('p', {}, `${texto(d.hostname)} `, selo(e.selo, e.rotulo))), fechar]);
    trocar($('gaveta-guias'), guias.map(([id, rotulo]) => {
      const b = el('button', { type: 'button', textContent: rotulo, role: 'tab', 'aria-selected': String(S.aba === id), 'data-k': `guia:${id}` });
      b.onclick = () => mudarAba(id);
      return b;
    }));
  }

  const corpo = [];
  const dado = (rotulo, valor, larga) => el('div', { className: larga ? 'larga' : '' }, el('dt', { textContent: rotulo }), el('dd', {}, valor));

  if (S.aba === 'resumo') {
    const semSessao = d.state === 'offline' || d.state === 'maintenance' || d.state === 'pending' || !dataDe(d.sessionStartedAt);
    corpo.push(el('dl', { className: 'dados' },
      dado('Em uso agora', apps.length ? el('div', { className: 'tags' }, ...apps.map((a) => el('span', { className: 'selo s-neutro', textContent: texto(a) }))) : 'Nenhum aplicativo aberto', true),
      dado('Tempo de atividade', semSessao ? 'Sem sessão' : duracao(Date.now() - dataDe(d.sessionStartedAt).getTime())),
      dado('Última comunicação', el('span', { className: 'num', textContent: haQuanto(d.lastSeenAt) })),
      dado('Windows', texto(d.windows) || 'Não informado'), dado('Alertas abertos', String(d.openAlerts || 0)),
      dado('Versão do Terminal', d.terminalVersion ? texto(d.terminalVersion) : 'Antiga (não informa a versão)')));

    const meus = (S.alerts || []).filter((a) => a.deviceId === d.id);
    if (meus.length) {
      const ver = el('button', { type: 'button', className: 'btn claro p', textContent: 'Ver nos alertas', 'data-k': 'ver-alertas' });
      ver.onclick = () => { fecharGaveta(); irSecao('alertas'); };
      corpo.push(el('div', { style: 'display:grid;gap:10px;justify-items:start' },
        el('ul', { className: 'lista-alertas', 'aria-label': 'Alertas abertos desta máquina' }, ...meus.slice(0, 5).map((a) => el('li', {}, ic('alert', 16), el('span', { textContent: `${texto(a.text)} (${quando(a.at)})` })))),
        pode('alerts.read') ? ver : null));
    }
    if (d.maintenance) corpo.push(el('p', { className: 'aviso-linha' }, ic('wrench', 18),
      el('span', { textContent: `Em manutenção até ${quando(d.maintenance.until).toLowerCase()}. Motivo: ${texto(d.maintenance.reason)}. A atendente vê a tela de manutenção.` })));

    if (d.state === 'pending') {
      corpo.push(el('p', { className: 'aviso-linha' }, ic('clock', 18), 'Esta máquina ainda aguarda aprovação na Visão geral.'));
    } else {
      const acoes = el('div', { className: 'acoes junto' });
      if (pode('maintenance.manage')) {
        const man = el('button', { type: 'button', className: d.maintenance ? 'btn' : 'btn claro', 'data-k': 'manut' }, ic('wrench', 18), d.maintenance ? 'Encerrar manutenção' : 'Manutenção');
        man.onclick = () => (d.maintenance
          ? agir(man, () => api('DELETE', `/devices/${encodeURIComponent(d.id)}/maintenance`), { recarregar: ['devices'], ok: `Manutenção encerrada. ${primeiro(nome)} já pode voltar a usar o computador.` })
          : abrirManutencao(d));
        acoes.append(man);
      }
      if (pode('devices.manage')) {
        const ren = el('button', { type: 'button', className: 'btn claro', 'data-k': 'renomear' }, ic('edit', 18), 'Renomear atendente');
        ren.onclick = () => abrirRenomear(d);
        acoes.append(ren);
      }
      // Visualização ao vivo: só computadores (o aplicativo do celular não envia a tela).
      if (pode('usage.read.individual') && !ehCelular(d)) {
        const ver = el('button', { type: 'button', className: 'btn claro', 'data-k': 'ver-tela' }, ic('eye', 18), 'Ver a tela');
        ver.onclick = () => abrirTela(d, nome);
        acoes.append(ver);
      }
      if (acoes.children.length) corpo.push(acoes);

      const ligado = d.observationMode !== false;
      const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(ligado), 'aria-labelledby': 'obs-titulo', 'aria-describedby': 'obs-texto', disabled: !pode('devices.manage'), 'data-k': 'obs' });
      chave.onclick = () => agir(chave, () => api('PATCH', `/devices/${encodeURIComponent(d.id)}`, { observationMode: !ligado }),
        { recarregar: ['devices'], ok: ligado ? 'Modo de observação desligado: endereços fora da lista passam a ser bloqueados.' : 'Modo de observação ligado: endereços fora da lista são permitidos e registrados.' });
      corpo.push(el('div', { className: 'obs' }, el('strong', { id: 'obs-titulo', textContent: 'Modo de observação' }), chave,
        el('p', { id: 'obs-texto', textContent: ligado
          ? 'Ligado: se a atendente entrar em um endereço fora da lista, a página abre e o domínio aparece em "Domínios observados" para você decidir. Use enquanto ainda está descobrindo os endereços que cada portal usa.'
          : 'Desligado: endereços fora da lista são bloqueados na hora, geram um alerta e um aviso urgente para o TI.' })));
    }
    corpo.push(el('p', { className: 'aviso-linha' }, ic('lock', 18), 'Você vê o status e os aplicativos em uso. A tela só é vista ao vivo, em "Ver a tela": a atendente recebe um aviso e a abertura fica na auditoria. Nada da tela é gravado, e o que ela digita não é registrado.'));
    if (pode('devices.revoke')) {
      const rem = el('button', { type: 'button', className: 'btn perigo', 'data-k': 'remover' }, ic('trash', 18), 'Remover máquina');
      rem.onclick = async () => {
        const sim = await confirmar({ titulo: `Remover ${texto(d.hostname)}?`, texto: `A máquina de ${nome} sai da Central e o Terminal dela para de funcionar na hora. Para voltar, será preciso cadastrar de novo com um código novo.`, botao: 'Remover máquina' });
        if (!sim) return;
        const feito = await agir(rem, () => api('DELETE', `/devices/${encodeURIComponent(d.id)}`), { recarregar: ['devices', 'alerts'] });
        if (feito) { fecharGaveta(); recado(`${texto(d.hostname)} foi removida.`); }
      };
      corpo.push(el('div', { className: 'zona-risco' }, rem, el('p', { textContent: 'O acesso desta máquina ao servidor é cancelado imediatamente.' })));
    }
  }

  if (S.aba === 'acesso') {
    const espera = situacaoLista('items');
    if (espera) corpo.push(el('p', { className: 'vazio', textContent: espera }));
    else {
      const itens = S.items.filter((s) => s.status !== 'pending' && s.enabled !== false), liberados = itens.filter((s) => temAcesso(s, d.id)).length;
      const maisSite = el('button', { type: 'button', className: 'btn p', 'data-k': 'mais-site' }, ic('plus', 16), 'Adicionar site');
      maisSite.onclick = () => novoSite([d.id]);
      const maisProg = el('button', { type: 'button', className: 'btn claro p', 'data-k': 'mais-prog' }, ic('plus', 16), 'Adicionar programa');
      maisProg.onclick = () => novoPrograma([d.id]);
      const botoes = [pode('apps.websites.manage') ? maisSite : null, pode('apps.executables.propose') ? maisProg : null].filter(Boolean);
      corpo.push(el('div', {}, el('p', {}, el('strong', { className: 'num forte', textContent: `${liberados} de ${itens.length}` }), ` liberados para ${primeiro(nome)}.`),
        botoes.length ? el('div', { className: 'acoes folga' }, ...botoes) : null));
      if (!itens.length) corpo.push(el('p', { className: 'vazio', textContent: 'Nenhum site ou programa liberado ainda.' }));
      else corpo.push(el('ul', { className: 'lista-acesso' }, ...itens.map((s) => {
        const tem = temAcesso(s, d.id);
        const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(tem), 'aria-label': `${texto(s.name)} para ${nome}`, disabled: !podeEditar(s), 'data-k': `acesso:${s.id}` });
        chave.onclick = () => agir(chave, () => api('PATCH', `/items/${encodeURIComponent(s.id)}`, { audience: novaAudiencia(s, d.id, !tem) }), { recarregar: ['items'] });
        return el('li', {}, avatar(s.name, 'q'), el('span', { className: 'n', textContent: texto(s.name) }), s.kind === 'program' ? selo('neutro', 'Programa') : null, chave);
      })));
    }
  }

  if (S.aba === 'historico') {
    const h = S.hist.id === d.id ? S.hist : { linhas: null, erro: null };
    if (!h.linhas) corpo.push(el('p', { className: 'vazio', textContent: h.erro ? `Não foi possível carregar o histórico. ${h.erro}` : 'Carregando…' }));
    else if (!h.linhas.length) corpo.push(el('p', { className: 'vazio', textContent: 'Nada registrado para esta máquina ainda.' }));
    else {
      const longo = h.linhas.some((l) => { const dt = dataDe(l.at); return dt && !mesmoDia(dt, new Date()); });
      corpo.push(el('ul', { className: `tempo${longo ? ' longo' : ''}` }, ...h.linhas.map((l) => el('li', {}, el('time', { dateTime: texto(l.at), textContent: quandoCurto(l.at) }), el('span', { textContent: texto(l.text) })))),
        el('p', { className: 'aviso-linha' }, ic('info', 18), 'Os 100 registros mais recentes desta máquina.'));
    }
  }

  const alvo = $('gaveta-corpo'), rolagem = alvo.scrollTop;
  trocar(alvo, corpo);
  alvo.scrollTop = rolagem;
  $('gaveta').hidden = false; $('gaveta-veu').hidden = false;
}

// ------------------------------------------------------- sites e programas
function desenharPropostas() {
  const propostas = (S.items || []).filter((s) => s.status === 'pending');
  const aprova = pode('apps.executables.approve');
  if (!mudou('propostas', [propostas, [...S.erroLinha], aprova, (S.devices || []).map((d) => [d.id, d.attendantName])])) return;
  trocar($('pendencias'), propostas.map((prog) => {
    const chave = `proposta:${prog.id}`, p = prog.proposal || {};
    const aprovar = el('button', { type: 'button', className: 'btn verde', 'data-k': `ok:${prog.id}` }, ic('check', 18), 'Aprovar');
    const recusar = el('button', { type: 'button', className: 'btn claro', textContent: 'Recusar', 'data-k': `nao:${prog.id}` });
    const tentar = async (botao, caminho, ok) => {
      botao.disabled = true;
      try {
        await api('POST', `/items/${encodeURIComponent(prog.id)}/${caminho}`);
        S.mut += 1; S.erroLinha.delete(chave);
        await carregar('items'); desenhar(); recado(ok);
      } catch (e) {
        if (e.status === 401) return;
        // O 403 de "quem propôs não pode aprovar" aparece aqui, junto da proposta.
        S.erroLinha.set(chave, e.message); botao.disabled = false; desenharPropostas();
      }
    };
    aprovar.onclick = () => tentar(aprovar, 'approve', `${texto(prog.name)} aprovado por duas pessoas, para ${noMeio(quemUsa(prog))}.`);
    recusar.onclick = async () => {
      if (await confirmar({ titulo: `Recusar ${texto(prog.name)}?`, texto: 'A proposta é removida. Para liberar o programa depois, será preciso propor de novo.', botao: 'Recusar proposta' })) tentar(recusar, 'reject', `A proposta de ${texto(prog.name)} foi recusada.`);
    };
    const erro = S.erroLinha.get(chave);
    return el('div', { className: 'pendencia' }, el('i', {}, ic('clock', 24)),
      el('div', {}, el('h3', { textContent: `${texto(prog.name)} aguarda a segunda aprovação` }),
        el('p', { textContent: `Proposto por ${texto(p.by) || 'outro administrador'} (${quando(p.at).toLowerCase()}), para ${noMeio(quemUsa(prog))}. Motivo: ${texto(p.reason) || 'não informado'}. Quem propôs não pode aprovar.` }),
        el('p', { className: 'dica', textContent: texto(prog.target) })),
      aprova ? aprovar : null, aprova ? recusar : null,
      erro ? el('p', { className: 'erro', role: 'alert', textContent: erro }) : null);
  }));
}

function desenharApps() {
  const termo = $('f-app').value.trim().toLowerCase();
  const nomes = (S.devices || []).map((d) => [d.id, d.attendantName, d.hostname, d.state]);
  if (!mudou('apps', [S.items, S.erro.items, S.fTipo, termo, nomes])) return;

  trocar($('f-tipo'), ['Todos', 'Sites', 'Programas'].map((t) => {
    const b = el('button', { type: 'button', textContent: t, 'aria-pressed': String(S.fTipo === t), 'data-k': `tipo:${t}` });
    b.onclick = () => { S.fTipo = t; desenharApps(); };
    return b;
  }));

  const colunas = ['Nome', 'Tipo', 'Quem usa', 'Situação', 'Liberado', ''];
  const cab = el('thead', {}, el('tr', {}, ...colunas.map((t) => (t ? el('th', { textContent: t, scope: 'col' }) : el('th', { scope: 'col', className: 'estreita' }, el('span', { className: 'so-leitor', textContent: 'Ações' }))))));
  const linhaUnica = (mensagem) => el('tbody', {}, el('tr', {}, el('td', { colSpan: colunas.length, className: 'vazio', textContent: mensagem })));
  const espera = situacaoLista('items');
  if (espera) { trocar($('tb-apps'), [cab, linhaUnica(espera)]); return; }

  const linhas = S.items.filter((s) => (S.fTipo === 'Todos' || (S.fTipo === 'Programas') === (s.kind === 'program'))
    && (!termo || texto(s.name).toLowerCase().includes(termo) || texto(s.target).toLowerCase().includes(termo) || (s.extraHosts || []).some((h) => texto(h).toLowerCase().includes(termo))));
  const corpo = el('tbody', {}, ...linhas.map((s) => {
    const edita = podeEditar(s), pendente = s.status === 'pending', programa = s.kind === 'program', liberado = s.enabled !== false && !pendente;
    const pessoas = (s.audience ? s.audience.map(maquina).filter(Boolean) : aprovadas()).map(nomeDe);
    const quem = el('button', { type: 'button', className: 'elo-quem', disabled: !edita || pendente, 'data-k': `quem:${s.id}` }, pessoas.length ? el('span', { className: 'pilha' }, ...pessoas.slice(0, 3).map((p) => avatar(p, 'm'))) : null, quemUsa(s));
    quem.setAttribute('aria-label', edita && !pendente ? `Alterar quem usa ${texto(s.name)}: ${quemUsa(s)}` : `Quem usa ${texto(s.name)}: ${quemUsa(s)}`);
    quem.onclick = () => editarAcesso(s);
    const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(liberado), 'aria-label': `${liberado ? 'Bloquear' : 'Liberar'} ${texto(s.name)}`, disabled: pendente || !edita, 'data-k': `liberado:${s.id}` });
    chave.onclick = () => agir(chave, () => api('PATCH', `/items/${encodeURIComponent(s.id)}`, { enabled: !liberado }), { recarregar: ['items'], ok: `${texto(s.name)} foi ${liberado ? 'bloqueado' : 'liberado'}.` });

    const acoes = el('div', { className: 'acoes-linha' });
    if (edita && !programa) {
      const editar = el('button', { type: 'button', className: 'redondo', 'aria-label': `Editar ${texto(s.name)} e os domínios adicionais`, title: 'Editar e domínios adicionais', 'data-k': `editar:${s.id}` }, ic('edit', 18));
      editar.onclick = () => editarSite(s);
      acoes.append(editar);
    }
    if (edita && !pendente) {
      const excluir = el('button', { type: 'button', className: 'redondo rubi', 'aria-label': `Excluir ${texto(s.name)}`, title: 'Excluir', 'data-k': `excluir:${s.id}` }, ic('trash', 18));
      excluir.onclick = async () => {
        const sim = await confirmar({ titulo: `Excluir ${texto(s.name)}?`, texto: `${programa ? 'O programa' : 'O site'} some da lista de todas as atendentes. Se for só por um tempo, prefira desligar o interruptor "Liberado".`, botao: 'Excluir' });
        if (sim) agir(excluir, () => api('DELETE', `/items/${encodeURIComponent(s.id)}`), { recarregar: ['items', 'observed'], ok: `${texto(s.name)} foi excluído.` });
      };
      acoes.append(excluir);
    }
    const extras = (s.extraHosts || []).length;
    return el('tr', {},
      el('td', {}, el('div', { className: 'celula' }, avatar(s.name, 'q'), el('div', { style: 'min-width:0' }, el('b', { textContent: texto(s.name) }),
        el('small', { textContent: texto(s.target), title: texto(s.target) }),
        extras ? el('small', { className: 'extras', textContent: `+ ${plural(extras, 'domínio adicional', 'domínios adicionais')}`, title: s.extraHosts.join('\n') }) : null))),
      el('td', { textContent: programa ? 'Programa' : 'Site' }), el('td', {}, quem),
      el('td', {}, pendente ? selo('pendente', 'Aguardando aprovação') : liberado ? selo('online', 'Liberado') : selo('bloqueado', 'Bloqueado')),
      el('td', {}, chave), el('td', { className: 'estreita' }, acoes));
  }));
  trocar($('tb-apps'), [cab, linhas.length ? corpo : linhaUnica(S.items.length ? 'Nada encontrado com esse filtro.' : 'Nenhum site ou programa cadastrado. Use "Adicionar site" para cadastrar o primeiro.')]);
}
$('f-app').oninput = desenharApps;

function desenharObservados() {
  const permite = pode('apps.websites.manage');
  const sites = (S.items || []).filter((s) => s.kind === 'site').map((s) => [s.id, s.name]);
  if (!mudou('observados', [S.observed, S.erro.observed, sites, permite])) return;
  const colunas = ['Domínio', 'Aberto a partir de', 'Máquina', 'Vezes', 'Última vez', ''];
  const cab = el('thead', {}, el('tr', {}, ...colunas.map((t) => (t ? el('th', { textContent: t, scope: 'col' }) : el('th', { scope: 'col', className: 'estreita' }, el('span', { className: 'so-leitor', textContent: 'Ações' }))))));
  const linhaUnica = (mensagem) => el('tbody', {}, el('tr', {}, el('td', { colSpan: colunas.length, className: 'vazio', textContent: mensagem })));
  const espera = situacaoLista('observed');
  if (espera) { trocar($('tb-observados'), [cab, linhaUnica(espera)]); return; }
  if (!S.observed.length) { trocar($('tb-observados'), [cab, linhaUnica('Nenhum domínio novo observado. Quando uma atendente entrar em um endereço fora da lista, ele aparece aqui.')]); return; }

  trocar($('tb-observados'), [cab, el('tbody', {}, ...S.observed.map((o) => {
    const chave = `obs:${o.host}:${o.itemId || ''}`;
    let escolha = null;
    if (!o.itemId && permite) {
      escolha = el('select', { className: 'campo p', 'aria-label': `Site que usa ${texto(o.host)}`, 'data-k': `sel:${chave}` }, el('option', { value: '', textContent: 'Escolha o site…' }), ...sites.map(([id, nome]) => el('option', { value: id, textContent: texto(nome) })));
      escolha.value = S.rascunho.get(chave) || '';
      escolha.onchange = () => S.rascunho.set(chave, escolha.value);
    }
    const permitir = el('button', { type: 'button', className: 'btn p', 'data-k': `permitir:${chave}` }, ic('check', 16), 'Permitir');
    permitir.setAttribute('aria-label', o.itemName ? `Permitir ${texto(o.host)} em ${texto(o.itemName)}` : `Permitir ${texto(o.host)}`);
    permitir.onclick = () => {
      const itemId = o.itemId || (escolha && escolha.value);
      if (!itemId) { recado('Escolha em qual site este domínio deve ser permitido.', true); if (escolha) escolha.focus(); return; }
      const destino = (sites.find(([id]) => id === itemId) || [])[1] || o.itemName || 'site';
      agir(permitir, () => api('POST', '/observed-hosts/allow', { host: o.host, itemId }), { recarregar: ['observed', 'items'], ok: `${texto(o.host)} agora é permitido em ${texto(destino)}.` }).then((feito) => { if (feito) S.rascunho.delete(chave); });
    };
    return el('tr', {},
      el('td', {}, el('span', { className: 'dominio', textContent: texto(o.host) })),
      el('td', {}, o.itemId ? texto(o.itemName) : escolha || texto(o.itemName) || 'Site não identificado',
        !o.itemId && escolha && o.itemName ? el('small', { className: 'dica', style: 'display:block;margin-top:4px', textContent: `Veio de ${texto(o.itemName)}, que não existe mais.` }) : null),
      el('td', { textContent: texto(o.deviceLabel) }),
      el('td', { className: 'num', textContent: texto(o.count) }),
      el('td', { textContent: quando(o.lastAt) }),
      el('td', { className: 'estreita' }, permite ? permitir : null));
  }))]);
}

// --------------------------------------------- alertas, notificações, auditoria
// ======================================================= aplicativos do computador
// O Terminal deixa a atendente abrir os aplicativos do computador; na primeira vez o pedido chega aqui. O TI libera ou
// bloqueia só para aquela atendente ou para todas; a decisão de uma atendente vale mais que a decisão para todas.
const nomeApp = (a) => { const d = a.deviceId && maquina(a.deviceId); return d ? nomeDe(d) : texto(a.deviceLabel) || 'a atendente'; };
// "Vale para" escolhido em cada linha esperando decisão (sobrevive à atualização da tabela a cada 4 s).
const quemApp = {};
function decidirAplicativo(botao, exe, decisao, deviceId = null, quem = '', nome = '') {
  const corpo = { exe, decision: decisao };
  if (deviceId) corpo.deviceId = deviceId;
  if (nome) corpo.name = nome;
  const para = deviceId ? `só para ${quem}` : 'para todas as atendentes';
  const rotulo = nome ? `${texto(nome)} (${texto(exe)})` : texto(exe);
  const ok = decisao === 'reset' ? `${rotulo}: ${quem} volta a seguir a decisão para todas.`
    : decisao === 'allow' ? `${rotulo} foi liberado ${para}.` : `${rotulo} foi bloqueado ${para}.`;
  return agir(botao, () => api('POST', '/device-apps/decide', corpo), { recarregar: ['aplicativos', 'alerts'], ok });
}
function desenharAplicativos() {
  const permite = pode('apps.executables.approve');
  const lista = S.aplicativos || [];
  const pendentes = lista.filter((a) => a.status === 'pending'), decididos = lista.filter((a) => a.status !== 'pending');
  const espera = situacaoLista('aplicativos');
  $('aplicativos-frase').textContent = espera || (pendentes.length
    ? `${plural(pendentes.length, 'pedido espera', 'pedidos esperam')} a sua decisão. Enquanto você não decide, o aplicativo continua como está: abrindo, ou bloqueado se já estava bloqueado para todas.`
    : 'Nenhum aplicativo esperando decisão. Quando uma atendente abrir pela primeira vez um aplicativo que não é programa cadastrado, ele aparece aqui com o nome dela.');
  $('aplicativos-novo').hidden = !permite;

  // Atendentes do formulário "Decidir um aplicativo" (mantém a escolha atual).
  const seletor = $('aplicativos-quem'), atual = seletor.value;
  const maquinas = aprovadas().map((d) => [d.id, nomeDe(d)]).sort((a, b) => a[1].localeCompare(b[1], 'pt-BR'));
  if (mudou('aplicativos-quem', maquinas)) {
    trocar(seletor, [el('option', { value: 'all', textContent: 'Todas as atendentes' }), ...maquinas.map(([id, nome]) => el('option', { value: id, textContent: `Só ${nome}` }))]);
    seletor.value = maquinas.some(([id]) => id === atual) ? atual : 'all';
  }

  if (!mudou('aplicativos', [S.aplicativos, S.erro.aplicativos, permite, maquinas, new Date().toDateString()])) return;
  desenharTabelasAplicativos(pendentes, decididos, espera, permite);
}
$('aplicativos-novo').addEventListener('submit', (e) => {
  e.preventDefault();
  const decisao = e.submitter && e.submitter.dataset.decisao;
  const campo = $('aplicativos-exe'), exe = campo.value.trim();
  const id = $('aplicativos-quem').value, d = id !== 'all' ? maquina(id) : null;
  if (!decisao) return;
  if (!exe) { recado('Escreva o nome do aplicativo, por exemplo spotify.exe.', true); campo.focus(); return; }
  decidirAplicativo(e.submitter, exe, decisao, d ? id : null, d ? nomeDe(d) : '').then((feito) => { if (feito) campo.value = ''; });
});

// As duas tabelas da aba Aplicativos: esperando decisão e já decididos.
function desenharTabelasAplicativos(pendentes, decididos, espera, permite) {
  const cabecalho = (colunas) => el('thead', {}, el('tr', {}, ...colunas.map((t) => (t ? el('th', { textContent: t, scope: 'col' }) : el('th', { scope: 'col', className: 'estreita' }, el('span', { className: 'so-leitor', textContent: 'Ações' }))))));
  const linhaUnica = (n, mensagem) => el('tbody', {}, el('tr', {}, el('td', { colSpan: n, className: 'vazio', textContent: mensagem })));
  const chave = (a) => `${a.scope}:${a.deviceId || ''}:${a.exe}`;
  // escopo(): devolve o deviceId (só ela) ou null (todas) no momento do clique.
  const botao = (a, decisao, escopo, quem) => {
    const liberar = decisao === 'allow', voltar = decisao === 'reset';
    const b = el('button', { type: 'button', className: voltar || liberar ? 'btn claro p' : 'btn perigo p', 'data-k': `aplicativos:${decisao}:${chave(a)}` },
      ic(voltar ? 'x' : liberar ? 'check' : 'lock', 16), voltar ? 'Tirar' : liberar ? 'Liberar' : 'Bloquear');
    b.setAttribute('aria-label', voltar ? `Tirar a decisão de ${texto(a.exe)} só para ${quem}` : `${liberar ? 'Liberar' : 'Bloquear'} ${texto(a.exe)}`);
    if (voltar) b.title = `Tira a decisão só para ${quem}: volta a valer a decisão para todas.`;
    b.onclick = () => decidirAplicativo(b, a.exe, decisao, escopo(), quem, a.name);
    return b;
  };
  const acoes = (...botoes) => el('td', { className: 'estreita' }, permite ? el('div', { style: 'display:flex;gap:8px' }, ...botoes) : null);
  const nada = '—';
  const arquivo = (a) => { const t = el('span', { className: 'dominio', textContent: texto(a.exe) }); if (a.path) t.title = texto(a.path, 300); return t; };
  const nome = (a) => el('b', { textContent: texto(a.name) || texto(a.exe) });

  const colP = ['Aplicativo', 'Arquivo', 'Atendente', 'Vezes', 'Última vez', 'Vale para', ''];
  trocar($('tb-aplicativos-pendentes'), [cabecalho(colP), espera ? linhaUnica(colP.length, espera) : !pendentes.length ? linhaUnica(colP.length, 'Tudo decidido.') : el('tbody', {}, ...pendentes.map((a) => {
    const quem = nomeApp(a), k = chave(a);
    const ela = a.scope === 'device' && a.deviceId;
    let vale = null;
    if (ela) {
      vale = el('select', { className: 'campo', 'data-k': `aplicativos-vale:${k}` },
        el('option', { value: 'one', textContent: `Só ${quem}` }), el('option', { value: 'all', textContent: 'Todas as atendentes' }));
      vale.setAttribute('aria-label', `Para quem vale a decisão sobre ${texto(a.exe)}`);
      vale.value = quemApp[k] || 'one';
      vale.disabled = !permite;
      vale.onchange = () => { quemApp[k] = vale.value; };
    }
    const escopo = () => (ela && (quemApp[k] || 'one') === 'one' ? a.deviceId : null);
    return el('tr', {},
      el('td', {}, nome(a), a.blockedForAll ? el('div', { style: 'margin-top:4px' }, selo('bloqueado', 'Bloqueado para todas: ela pediu')) : null),
      el('td', {}, arquivo(a)),
      el('td', { textContent: a.deviceLabel ? quem : nada }),
      el('td', { className: 'num', textContent: String(a.opens || 0) }),
      el('td', { textContent: a.lastAt ? quando(a.lastAt) : nada }),
      el('td', {}, vale || 'Todas as atendentes'),
      acoes(botao(a, 'block', escopo, quem), botao(a, 'allow', escopo, quem)));
  }))]);

  const colD = ['Aplicativo', 'Arquivo', 'Vale para', 'Situação', 'Decidido por', 'Quando', ''];
  trocar($('tb-aplicativos-decididos'), [cabecalho(colD), espera ? linhaUnica(colD.length, espera) : !decididos.length ? linhaUnica(colD.length, 'Nenhum aplicativo decidido ainda.') : el('tbody', {}, ...decididos.map((a) => {
    const bloqueado = a.status === 'blocked', ela = a.scope === 'device' && a.deviceId, quem = nomeApp(a);
    const escopo = () => (ela ? a.deviceId : null);
    return el('tr', {},
      el('td', {}, nome(a)),
      el('td', {}, arquivo(a)),
      el('td', { textContent: ela ? `Só ${quem}` : 'Todas as atendentes' }),
      el('td', {}, bloqueado ? selo('bloqueado', 'Bloqueado') : selo('online', 'Liberado')),
      el('td', { textContent: texto(a.decidedBy) || nada }),
      el('td', { textContent: a.decidedAt ? quando(a.decidedAt) : nada }),
      ela ? acoes(botao(a, bloqueado ? 'allow' : 'block', escopo, quem), botao(a, 'reset', escopo, quem))
        : acoes(botao(a, bloqueado ? 'allow' : 'block', escopo, quem)));
  }))]);
}

// ======================================================= sites nos navegadores
// O Terminal avisa quando a atendente abre num navegador um site que não é operacional. O TI libera ou bloqueia
// só para aquela atendente ou para todas; a decisão de uma atendente vale mais que a decisão para todas.
const nomeChrome = (c) => { const d = c.deviceId && maquina(c.deviceId); return d ? nomeDe(d) : texto(c.deviceLabel) || 'a atendente'; };
// "Vale para" escolhido em cada linha esperando decisão (sobrevive à atualização da tabela a cada 4 s).
const quemChrome = {};
function decidirChrome(botao, host, decisao, deviceId = null, quem = '') {
  const corpo = { host, decision: decisao };
  if (deviceId) corpo.deviceId = deviceId;
  const para = deviceId ? `só para ${quem}` : 'para todas as atendentes';
  const ok = decisao === 'reset' ? `${texto(host)}: ${quem} volta a seguir a decisão para todas.`
    : decisao === 'allow' ? `${texto(host)} foi liberado ${para}.` : `${texto(host)} foi bloqueado ${para}.`;
  return agir(botao, () => api('POST', '/chrome-sites/decide', corpo), { recarregar: ['chrome', 'alerts'], ok });
}
function desenharChrome() {
  const permite = pode('apps.websites.manage');
  const lista = S.chrome || [];
  const pendentes = lista.filter((c) => c.status === 'pending'), decididos = lista.filter((c) => c.status !== 'pending');
  const espera = situacaoLista('chrome');
  $('chrome-frase').textContent = espera || (pendentes.length
    ? `${plural(pendentes.length, 'pedido espera', 'pedidos esperam')} a sua decisão. Enquanto você não decide, o site continua como está: abrindo, ou bloqueado se já estava bloqueado para todas.`
    : 'Nenhum site esperando decisão. Quando uma atendente abrir num navegador um site fora da lista, ele aparece aqui com o nome dela.');
  $('chrome-novo').hidden = !permite;

  // Atendentes do formulário "Decidir um domínio" (mantém a escolha atual).
  const seletor = $('chrome-quem'), atual = seletor.value;
  const maquinas = aprovadas().map((d) => [d.id, nomeDe(d)]).sort((a, b) => a[1].localeCompare(b[1], 'pt-BR'));
  if (mudou('chrome-quem', maquinas)) {
    trocar(seletor, [el('option', { value: 'all', textContent: 'Todas as atendentes' }), ...maquinas.map(([id, nome]) => el('option', { value: id, textContent: `Só ${nome}` }))]);
    seletor.value = maquinas.some(([id]) => id === atual) ? atual : 'all';
  }

  if (!mudou('chrome', [S.chrome, S.erro.chrome, permite, maquinas, new Date().toDateString()])) return;

  const cabecalho = (colunas) => el('thead', {}, el('tr', {}, ...colunas.map((t) => (t ? el('th', { textContent: t, scope: 'col' }) : el('th', { scope: 'col', className: 'estreita' }, el('span', { className: 'so-leitor', textContent: 'Ações' }))))));
  const linhaUnica = (n, mensagem) => el('tbody', {}, el('tr', {}, el('td', { colSpan: n, className: 'vazio', textContent: mensagem })));
  const tempo = (min) => (!min ? 'menos de 1 min' : min < 60 ? `${min} min` : `${Math.floor(min / 60)} h ${min % 60} min`);
  const chave = (c) => `${c.scope}:${c.deviceId || ''}:${c.host}`;
  // escopo(): devolve o deviceId (só ela) ou null (todas) no momento do clique.
  const botao = (c, decisao, escopo, quem) => {
    const liberar = decisao === 'allow', voltar = decisao === 'reset';
    const b = el('button', { type: 'button', className: voltar || liberar ? 'btn claro p' : 'btn perigo p', 'data-k': `chrome:${decisao}:${chave(c)}` },
      ic(voltar ? 'x' : liberar ? 'check' : 'lock', 16), voltar ? 'Tirar' : liberar ? 'Liberar' : 'Bloquear');
    b.setAttribute('aria-label', voltar ? `Tirar a decisão de ${texto(c.host)} só para ${quem}` : `${liberar ? 'Liberar' : 'Bloquear'} ${texto(c.host)}`);
    if (voltar) b.title = `Tira a decisão só para ${quem}: volta a valer a decisão para todas.`;
    b.onclick = () => decidirChrome(b, c.host, decisao, escopo(), quem);
    return b;
  };
  const acoes = (...botoes) => el('td', { className: 'estreita' }, permite ? el('div', { style: 'display:flex;gap:8px' }, ...botoes) : null);
  const nada = '—';

  const colP = ['Domínio', 'Atendente', 'Navegador', 'Vezes', 'Tempo aberto', 'Última vez', 'Vale para', ''];
  trocar($('tb-chrome-pendentes'), [cabecalho(colP), espera ? linhaUnica(colP.length, espera) : !pendentes.length ? linhaUnica(colP.length, 'Tudo decidido.') : el('tbody', {}, ...pendentes.map((c) => {
    const quem = nomeChrome(c), k = chave(c);
    const ela = c.scope === 'device' && c.deviceId;
    let vale = null;
    if (ela) {
      vale = el('select', { className: 'campo', 'data-k': `chrome-vale:${k}` },
        el('option', { value: 'one', textContent: `Só ${quem}` }), el('option', { value: 'all', textContent: 'Todas as atendentes' }));
      vale.setAttribute('aria-label', `Para quem vale a decisão sobre ${texto(c.host)}`);
      vale.value = quemChrome[k] || 'one';
      vale.disabled = !permite;
      vale.onchange = () => { quemChrome[k] = vale.value; };
    }
    const escopo = () => (ela && (quemChrome[k] || 'one') === 'one' ? c.deviceId : null);
    return el('tr', {},
      el('td', {}, el('span', { className: 'dominio', textContent: texto(c.host) }),
        c.blockedForAll ? el('div', { style: 'margin-top:4px' }, selo('bloqueado', 'Bloqueado para todas: ela pediu')) : null),
      el('td', { textContent: c.deviceLabel ? quem : nada }),
      el('td', { textContent: texto(c.browser) || nada }),
      el('td', { className: 'num', textContent: texto(c.visits) }),
      el('td', { className: 'num', textContent: tempo(c.minutes) }),
      el('td', { textContent: c.lastAt ? quando(c.lastAt) : nada }),
      el('td', {}, vale || 'Todas as atendentes'),
      acoes(botao(c, 'block', escopo, quem), botao(c, 'allow', escopo, quem)));
  }))]);

  const colD = ['Domínio', 'Vale para', 'Situação', 'Decidido por', 'Quando', ''];
  trocar($('tb-chrome-decididos'), [cabecalho(colD), espera ? linhaUnica(colD.length, espera) : !decididos.length ? linhaUnica(colD.length, 'Nenhum domínio decidido ainda.') : el('tbody', {}, ...decididos.map((c) => {
    const bloqueado = c.status === 'blocked', ela = c.scope === 'device' && c.deviceId, quem = nomeChrome(c);
    const escopo = () => (ela ? c.deviceId : null);
    return el('tr', {},
      el('td', {}, el('span', { className: 'dominio', textContent: texto(c.host) })),
      el('td', { textContent: ela ? `Só ${quem}` : 'Todas as atendentes' }),
      el('td', {}, bloqueado ? selo('bloqueado', 'Bloqueado') : selo('online', 'Liberado')),
      el('td', { textContent: texto(c.decidedBy) || nada }),
      el('td', { textContent: c.decidedAt ? quando(c.decidedAt) : nada }),
      ela ? acoes(botao(c, bloqueado ? 'allow' : 'block', escopo, quem), botao(c, 'reset', escopo, quem))
        : acoes(botao(c, bloqueado ? 'allow' : 'block', escopo, quem)));
  }))]);
}
$('chrome-novo').addEventListener('submit', (e) => {
  e.preventDefault();
  const decisao = e.submitter && e.submitter.dataset.decisao;
  const campo = $('chrome-dominio'), host = campo.value.trim();
  const id = $('chrome-quem').value, d = id !== 'all' ? maquina(id) : null;
  if (!decisao) return;
  if (!host) { recado('Escreva o domínio, por exemplo jogos.com.', true); campo.focus(); return; }
  decidirChrome(e.submitter, host, decisao, d ? id : null, d ? nomeDe(d) : '').then((feito) => { if (feito) campo.value = ''; });
});

function desenharAlertas() {
  const espera = situacaoLista('alerts'), lista = S.alerts || [], n = lista.length;
  $('alertas-frase').textContent = espera || (n ? `${plural(n, 'alerta aberto', 'alertas abertos')}. Resolva cada um depois de verificar.` : 'Nenhum alerta aberto no momento.');
  if (!mudou('alertas', [S.alerts, S.erro.alerts, new Date().toDateString()])) return;
  trocar($('ls-alertas'), espera || !n ? [el('li', { className: 'vazio', textContent: espera || 'Tudo em ordem. Quando algo precisar de atenção, aparece aqui.' })] : lista.map((a) => {
    const ok = el('button', { type: 'button', className: 'btn claro p', textContent: 'Resolver', 'data-k': `resolver:${a.id}` });
    ok.setAttribute('aria-label', `Resolver o alerta de ${texto(a.deviceLabel)}`);
    ok.onclick = () => agir(ok, () => api('POST', `/alerts/${encodeURIComponent(a.id)}/resolve`), { recarregar: ['alerts', 'devices'], ok: 'Alerta resolvido.' });
    const ver = el('button', { type: 'button', className: 'btn claro p', textContent: 'Ver máquina', 'data-k': `ver:${a.id}` });
    ver.onclick = () => { irSecao('visao'); abrirGaveta(a.deviceId); };
    const i = el('i', { style: 'background:var(--ambar-100);color:var(--ambar-700)' }, ic('alert', 20));
    return el('li', {}, i, el('div', {}, el('strong', { textContent: texto(a.deviceLabel) || 'Máquina' }), el('div', { textContent: texto(a.text), style: 'color:var(--texto)' }), el('time', { dateTime: texto(a.at), textContent: quando(a.at) })),
      maquina(a.deviceId) ? ver : null, pode('alerts.handle') ? ok : null);
  }));
}
function desenharNotificacoes() {
  if (!mudou('notif', [S.notifs, S.erro.notifs, [...S.novas], new Date().toDateString()])) return;
  const espera = situacaoLista('notifs'), lista = S.notifs || [];
  trocar($('ls-notif'), espera || !lista.length ? [el('li', { className: 'vazio', textContent: espera || 'Nenhuma notificação ainda. Elas aparecem aqui conforme as atendentes usam os computadores.' })] : lista.map((n) => {
    const nova = !n.read || S.novas.has(n.id);
    const i = el('i', { style: n.urgent ? 'background:var(--rubi-100);color:var(--rubi-700)' : 'background:var(--azul-50);color:var(--elo)' }, ic(n.urgent ? 'alert' : 'bell', 20));
    return el('li', {}, i, el('div', { style: nova ? 'font-weight:600' : '' }, texto(n.text), el('time', { dateTime: texto(n.at), textContent: quando(n.at), style: 'font-weight:400' })),
      n.urgent ? selo('bloqueado', 'Urgente') : null, nova ? selo('suporte', 'Nova') : null);
  }));
}
function desenharAuditoria() {
  if (!mudou('auditoria', [S.audit, S.erro.audit, S.verificacao, new Date().toDateString()])) return;
  const v = S.verificacao;
  $('aud-resultado').replaceChildren(...(!v ? [] : [el('p', { className: `resultado ${v.bom ? 'bom' : 'ruim'}` }, ic(v.bom ? 'shield' : 'alert', 18), el('span', { textContent: v.texto }))]));
  const espera = situacaoLista('audit'), lista = S.audit || [];
  trocar($('ls-auditoria'), espera || !lista.length ? [el('li', { className: 'vazio', textContent: espera || 'Nenhuma ação registrada ainda.' })] : lista.map((a) => {
    const i = el('i', { style: 'background:var(--azul-50);color:var(--elo)' }, ic('history', 20));
    return el('li', {}, i, el('div', {}, texto(a.text), el('time', { dateTime: texto(a.at), textContent: [quando(a.at), texto(a.actor), `registro nº ${texto(a.sequence)}`].filter(Boolean).join(' · ') })));
  }));
}
// O servidor informa o tipo da falha por um código; aqui ele vira uma frase.
const FALHA_AUDITORIA = {
  hashMismatch: 'O conteúdo deste registro não confere com a assinatura dele: ele foi alterado depois de gravado.',
  previousHashMismatch: 'Este registro não se encaixa no anterior: algum registro foi alterado ou removido.',
  sequenceGap: 'Falta um registro na sequência: algum foi removido.',
};
$('aud-verificar').onclick = async () => {
  const botao = $('aud-verificar');
  botao.disabled = true;
  try {
    const [r] = await Promise.all([api('GET', '/audit/verify'), carregar('audit')]);
    S.verificacao = r && r.intact
      ? { bom: true, texto: `Registro íntegro. Nenhuma alteração encontrada na cadeia (verificado às ${hhmm(new Date())}).` }
      : { bom: false, texto: `Falha de integridade${r && r.failedSequence !== null && r.failedSequence !== undefined ? ` no registro nº ${r.failedSequence}` : ''}. ${FALHA_AUDITORIA[r && r.failure] || texto(r && r.failure) || 'O registro pode ter sido alterado fora do sistema.'}` };
  } catch (e) {
    if (e.status === 401) return;
    S.verificacao = { bom: false, texto: `Não foi possível verificar. ${e.message}` };
  } finally { botao.disabled = false; }
  desenharAuditoria();
};

// ---------------------------------------------------------- administradores
function desenharAdmins() {
  const lista = S.admins || [];
  $('adm-frase').textContent = situacaoLista('admins') || `${plural(lista.length, 'conta', 'contas')}. Só o superadministrador cria, apaga e troca a senha de contas.`;
  if (!mudou('admins', [S.admins, S.erro.admins])) return;
  const colunas = ['Nome', 'E-mail', 'Função', 'Situação', ''];
  const cab = el('thead', {}, el('tr', {}, ...colunas.map((t) => (t ? el('th', { textContent: t, scope: 'col' }) : el('th', { scope: 'col', className: 'estreita' }, el('span', { className: 'so-leitor', textContent: 'Ações' }))))));
  const espera = situacaoLista('admins');
  trocar($('tb-admins'), [cab, el('tbody', {}, ...(espera ? [el('tr', {}, el('td', { colSpan: 5, className: 'vazio', textContent: espera }))] : lista.map((a) => el('tr', {},
    el('td', {}, el('div', { className: 'celula' }, avatar(a.name), el('div', {}, el('b', { textContent: texto(a.name) }), a.id === S.me.id ? el('small', { textContent: 'Você' }) : null))),
    el('td', { textContent: texto(a.email) }),
    el('td', {}, el('span', { textContent: FUNCOES[a.role] || texto(a.role), title: FUNCAO_DICA[a.role] || '' })),
    el('td', {}, a.active === false ? selo('offline', 'Inativo') : selo('online', 'Ativo')),
    el('td', { className: 'estreita' }, acoesAdmin(a))))))]);
}
// Trocar a senha e apagar: a tela só aparece para o superadministrador, e o servidor confere de novo.
function acoesAdmin(a) {
  const nome = texto(a.name), eu = a.id === S.me.id;
  const senha = el('button', { type: 'button', className: 'redondo', 'aria-label': `Trocar a senha de ${nome}`, title: 'Trocar a senha', 'data-k': `senha:${a.id}` }, ic('lock', 18));
  senha.onclick = () => trocarSenha(a);
  if (eu) return el('div', { className: 'acoes-linha' }, senha);
  const apagar = el('button', { type: 'button', className: 'redondo rubi', 'aria-label': `Apagar a conta de ${nome}`, title: 'Apagar a conta', 'data-k': `apagar:${a.id}` }, ic('trash', 18));
  apagar.onclick = async () => {
    const sim = await confirmar({ titulo: `Apagar a conta de ${nome}?`, texto: `${nome} (${texto(a.email)}) não consegue mais entrar na Central e, se estiver logado agora, sai na hora. Isso não pode ser desfeito; para voltar, será preciso criar a conta de novo.`, botao: 'Apagar conta' });
    if (sim) agir(apagar, () => api('DELETE', `/admins/${encodeURIComponent(a.id)}`), { recarregar: ['admins'], ok: `A conta de ${nome} foi apagada.` });
  };
  return el('div', { className: 'acoes-linha' }, senha, apagar);
}
let alvoSenha = null;
function trocarSenha(a) {
  alvoSenha = a;
  $('senha-texto').textContent = a.id === S.me.id
    ? 'Defina a sua senha nova. Você continua logado aqui; outras sessões suas são encerradas.'
    : `Defina a senha nova de ${texto(a.name)} (${texto(a.email)}). Se a pessoa estiver logada, ela sai e precisa entrar com a senha nova.`;
  abrirFormulario('dlg-senha', ['senha-nova', 'senha-nova2'], 'senha-erro');
}
aoEnviar('dlg-senha', 'senha-erro', async () => {
  const senha = $('senha-nova').value;
  exigir(senha.length >= 10, 'A senha precisa ter no mínimo 10 caracteres.');
  exigir(senha === $('senha-nova2').value, 'As duas senhas não são iguais.');
  await api('POST', `/admins/${encodeURIComponent(alvoSenha.id)}/password`, { password: senha });
  $('senha-nova').value = ''; $('senha-nova2').value = '';
  return alvoSenha.id === S.me.id ? 'Sua senha foi trocada.' : `A senha de ${texto(alvoSenha.name)} foi trocada.`;
});

// ====================================================== diálogos e formulários
function confirmar({ titulo, texto: mensagem, botao }) {
  return new Promise((resolver) => {
    const dlg = $('dlg-confirmar');
    $('conf-titulo').textContent = titulo; $('conf-texto').textContent = mensagem; $('conf-ok').textContent = botao;
    let resposta = false;
    dlg.querySelector('form').onsubmit = (e) => { e.preventDefault(); resposta = true; dlg.close(); };
    dlg.addEventListener('close', () => resolver(resposta), { once: true });
    dlg.showModal();
    dlg.querySelector('[data-fechar]').focus();   // o padrão de uma ação destrutiva é cancelar
  });
}
// Liga o envio de um formulário de diálogo: valida, chama a API, mostra o erro do servidor
// dentro do próprio diálogo e só fecha quando deu certo.
function aoEnviar(idDialogo, idErro, enviar) {
  const dlg = $(idDialogo), form = dlg.querySelector('form'), botao = form.querySelector('[type="submit"]');
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    $(idErro).hidden = true;
    botao.disabled = true;
    try {
      const mensagem = await enviar();
      S.pendente = true;
      dlg.close();
      if (mensagem) recado(mensagem);
    } catch (erro) {
      if (erro.status !== 401) falharEm(idErro, erro.message);
    } finally { botao.disabled = false; }
  });
}
const exigir = (condicao, mensagem) => { if (!condicao) throw new Error(mensagem); };

// "Para quem": todas as atendentes ou só as escolhidas.
function montarQuem(id, escolhidas) {
  const lista = aprovadas();
  const radio = (valor, marcado, rotulo) => el('label', {}, el('input', { type: 'radio', name: id, value: valor, checked: marcado }), rotulo);
  const pessoas = el('div', { className: 'pessoas' }, ...(lista.length ? lista.map((d) => {
    const caixa = el('input', { type: 'checkbox', value: d.id, checked: Boolean(escolhidas && escolhidas.includes(d.id)) });
    caixa.onchange = () => { $(id).querySelector('input[value="algumas"]').checked = true; };
    return el('label', { title: `${nomeDe(d)} (${texto(d.hostname)})` }, caixa, avatar(nomeDe(d), 'm'), el('span', { className: 'n', textContent: nomeDe(d) }));
  }) : [el('p', { className: 'nada', textContent: 'Nenhuma máquina aprovada ainda.' })]));
  $(id).replaceChildren(el('strong', { id: `${id}-rotulo`, textContent: 'Para quem' }),
    el('div', { className: 'opcoes', role: 'radiogroup', 'aria-labelledby': `${id}-rotulo` }, radio('todas', !escolhidas, 'Todas as atendentes'), radio('algumas', Boolean(escolhidas), 'Só as escolhidas')), pessoas);
  // Ids que não aparecem na lista (máquina ainda pendente, por exemplo) são mantidos ao salvar.
  $(id).dataset.ocultos = JSON.stringify((escolhidas || []).filter((x) => !lista.some((d) => d.id === x)));
}
function lerQuem(id) {
  if ($(id).querySelector('input[value="todas"]').checked) return null;
  const marcadas = [...$(id).querySelectorAll('input[type="checkbox"]:checked')].map((c) => c.value);
  exigir(marcadas.length > 0, 'Escolha pelo menos uma atendente.');
  if (marcadas.length === aprovadas().length) return null;   // todas marcadas é o mesmo que "todas"
  let ocultos = [];
  try { ocultos = JSON.parse($(id).dataset.ocultos || '[]'); } catch { ocultos = []; }
  return [...marcadas, ...ocultos];
}
function limparDominios(bruto) {
  return [...new Set(bruto.split(/[\n,;]+/).map((l) => l.trim()).filter(Boolean).map((linha) => {
    let host = '';
    try { host = new URL(/^[a-z][a-z0-9+.-]*:\/\//i.test(linha) ? linha : `https://${linha}`).hostname.toLowerCase(); } catch { host = ''; }
    exigir(host && !/\s/.test(linha), `"${linha}" não é um domínio válido. Escreva só o endereço, como login.banco.com.br.`);
    return host;
  }))];
}
function abrirFormulario(id, campos, erro) {
  for (const c of campos) $(c).value = '';
  $(erro).hidden = true;
  $(id).showModal(); $(campos[0]).focus();
}
function novoSite(escolhidas = null) { montarQuem('site-quem', escolhidas); $('site-grupo').selectedIndex = 0; abrirFormulario('dlg-site', ['site-nome', 'site-end', 'site-extras'], 'site-erro'); }
function novoPrograma(escolhidas = null) { montarQuem('prog-quem', escolhidas); abrirFormulario('dlg-prog', ['prog-nome', 'prog-caminho', 'prog-motivo'], 'prog-erro'); }
$('novo-site').onclick = () => novoSite();
$('novo-prog').onclick = () => novoPrograma();

aoEnviar('dlg-site', 'site-erro', async () => {
  const nome = $('site-nome').value.trim(), endereco = $('site-end').value.trim();
  exigir(nome, 'Informe o nome que a atendente vai ver.');
  let url = null;
  try { url = new URL(/^[a-z][a-z0-9+.-]*:\/\//i.test(endereco) ? endereco : `https://${endereco}`); } catch { url = null; }
  exigir(endereco && url && /^https?:$/.test(url.protocol) && url.hostname && !/\s/.test(endereco), 'Informe um endereço válido, como portal.banco.com.br/login.');
  const extraHosts = limparDominios($('site-extras').value), audience = lerQuem('site-quem');
  const novo = await api('POST', '/items/sites', { name: nome, url: url.href, group: $('site-grupo').value, audience, extraHosts });
  S.mut += 1;
  await carregar('items');
  return `${nome} foi adicionado para ${noMeio(quemUsa(novo || { audience }))} e já aparece no Terminal.`;
});
aoEnviar('dlg-prog', 'prog-erro', async () => {
  const nome = $('prog-nome').value.trim(), caminho = $('prog-caminho').value.trim(), motivo = $('prog-motivo').value.trim();
  exigir(nome, 'Informe o nome do programa.');
  exigir(/\.exe$/i.test(caminho), 'Informe o caminho completo do executável, terminando em .exe.');
  exigir(!/(^|[\\/])(cmd|powershell|pwsh|wscript|cscript|mshta|regedit|rundll32|msiexec|bash|wsl|wt)\.exe$/i.test(caminho), 'Este programa está na lista fixa de bloqueio e não pode ser liberado.');
  exigir(motivo, 'A justificativa é obrigatória.');
  const audience = lerQuem('prog-quem');
  const criado = await api('POST', '/items/programs', { name: nome, path: caminho, reason: motivo, audience });
  S.mut += 1;
  await carregar('items');
  if (S.secao !== 'apps') { fecharGaveta(); irSecao('apps'); }
  if (criado && criado.status === 'active') return `${nome} foi liberado direto (superadmin) e já aparece no Terminal.`;
  return `Proposta enviada. ${nome} só fica disponível depois que outro administrador aprovar.`;
});

let editando = null;
function editarAcesso(item) {
  editando = item;
  $('acesso-titulo').textContent = `Quem usa ${texto(item.name)}`; $('acesso-erro').hidden = true;
  montarQuem('acesso-quem', item.audience || null);
  $('dlg-acesso').showModal();
}
aoEnviar('dlg-acesso', 'acesso-erro', async () => {
  const audience = lerQuem('acesso-quem');
  const salvo = await api('PATCH', `/items/${encodeURIComponent(editando.id)}`, { audience });
  S.mut += 1;
  await carregar('items');
  return `Acesso a ${texto(editando.name)} atualizado: ${noMeio(quemUsa(salvo || { audience }))}.`;
});
function editarSite(item) {
  editando = item;
  $('editar-texto').textContent = texto(item.target); $('editar-erro').hidden = true;
  $('editar-nome').value = texto(item.name);
  $('editar-grupo').value = GRUPOS.includes(item.group) ? item.group : GRUPOS[0];
  $('editar-extras').value = (item.extraHosts || []).join('\n');
  $('dlg-editar').showModal(); $('editar-extras').focus();
}
aoEnviar('dlg-editar', 'editar-erro', async () => {
  const nome = $('editar-nome').value.trim();
  exigir(nome, 'Informe o nome que a atendente vai ver.');
  const extraHosts = limparDominios($('editar-extras').value);
  await api('PATCH', `/items/${encodeURIComponent(editando.id)}`, { name: nome, group: $('editar-grupo').value, extraHosts });
  S.mut += 1;
  await carregar('items');
  return `${nome} foi atualizado. ${extraHosts.length ? plural(extraHosts.length, 'domínio adicional permitido', 'domínios adicionais permitidos') : 'Nenhum domínio adicional'}.`;
});

let alvoMaquina = null;
function abrirManutencao(d) {
  alvoMaquina = d;
  $('man-texto').textContent = `${primeiro(nomeDe(d))} vê uma tela de manutenção em ${texto(d.hostname)} até o prazo terminar. Você pode encerrar antes.`;
  $('man-prazo').value = '60';
  abrirFormulario('dlg-manut', ['man-motivo'], 'man-erro');
}
aoEnviar('dlg-manut', 'man-erro', async () => {
  const motivo = $('man-motivo').value.trim();
  exigir(motivo, 'Informe o motivo da manutenção.');
  const salvo = await api('POST', `/devices/${encodeURIComponent(alvoMaquina.id)}/maintenance`, { reason: motivo, minutes: Number($('man-prazo').value) });
  S.mut += 1;
  await carregar('devices');
  const ate = salvo && salvo.maintenance ? ` até ${quando(salvo.maintenance.until).toLowerCase()}` : '';
  return `${texto(alvoMaquina.hostname)} está em manutenção${ate}.`;
});
function abrirRenomear(d) {
  alvoMaquina = d;
  $('ren-texto').textContent = `É o nome que aparece na Central e no Terminal de ${texto(d.hostname)}.`;
  $('ren-erro').hidden = true; $('ren-nome').value = texto(d.attendantName);
  $('dlg-renomear').showModal(); $('ren-nome').select();
}
aoEnviar('dlg-renomear', 'ren-erro', async () => {
  const nome = $('ren-nome').value.trim();
  exigir(nome, 'Informe o nome da atendente.');
  await api('PATCH', `/devices/${encodeURIComponent(alvoMaquina.id)}`, { attendantName: nome });
  S.mut += 1;
  await carregar('devices');
  return `Atendente de ${texto(alvoMaquina.hostname)} renomeada para ${nome}.`;
});

$('novo-admin').onclick = () => { $('adm-funcao').value = 'admin'; $('adm-funcao-dica').textContent = FUNCAO_DICA.admin; abrirFormulario('dlg-admin', ['adm-nome', 'adm-email', 'adm-senha'], 'adm-erro'); };
$('adm-funcao').onchange = () => { $('adm-funcao-dica').textContent = FUNCAO_DICA[$('adm-funcao').value] || ''; };
aoEnviar('dlg-admin', 'adm-erro', async () => {
  const nome = $('adm-nome').value.trim(), email = $('adm-email').value.trim(), senha = $('adm-senha').value, funcao = $('adm-funcao').value;
  exigir(nome, 'Informe o nome.');
  exigir(/^\S+@\S+\.\S+$/.test(email), 'Informe um e-mail válido.');
  exigir(senha.length >= 10, 'A senha precisa ter no mínimo 10 caracteres.');
  await api('POST', '/admins', { name: nome, email, password: senha, role: funcao });
  $('adm-senha').value = '';
  S.mut += 1;
  await carregar('admins');
  return `Conta de ${nome} criada, com a função ${FUNCOES[funcao].toLowerCase()}.`;
});

// =================================================================== início
iniciar();
