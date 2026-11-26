// Central Neural — aba Margem liberada (SIAPE). Todo começo de mês a Central lê a base de margens do SIAPE (SQL Server
// na máquina da Kamila), acha quem tem valor liberado total acima de R$ 1.000 e positivação "NÃO", a IA escreve como a atendente e
// ela sai pelo CRM: por padrão depois de aprovada em Mensagens para aprovar. Usa as funções e o estado de neural.js e
// aprovar.js (carregados antes). Mesmas regras: nada do servidor entra como HTML.
//
// Desenho da tela: em cima o painel da rodada (quantas mensagens o mês já gastou, botões de agir e a lista do que falta
// para rodar); embaixo, à esquerda, a configuração em três abas (ligação, bases e regras, saída pelo CRM) com um só
// Salvar; à direita, as últimas mensagens e o "como funciona".
'use strict';

E.siape = null;
E.siapeAba = E.siapeAba || 'ligacao';

async function carregarSiape() {
  try { E.siape = await api('GET', '/neural/siape'); } catch (e) { recado(e.message, true); return; }
  if (E.vista === 'siape') desenharSiape();
}

const reais = (n) => Number(n || 0).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
const mesPorExtenso = () => { const m = new Date().toLocaleDateString('pt-BR', { month: 'long', year: 'numeric' }); return m.charAt(0).toUpperCase() + m.slice(1); };
const titulinho = (t) => el('h3', { className: 'siape-titulinho', textContent: t });

function desenharSiape() {
  const s = E.siape;
  if (!s) { $('v-siape').replaceChildren(el('p', { className: 'nota', textContent: 'Carregando…' })); carregarSiape(); return; }
  const tabelas = s.tables ? s.tables.list : [];
  $('v-siape').replaceChildren(
    painelRodadaSiape(s, tabelas),
    el('div', { className: 'siape-layout' },
      cartaoConfiguracaoSiape(s, tabelas),
      el('div', { className: 'siape-coluna' }, cartaoMensagensSiape(s), cartaoComoFuncionaSiape())));
}

// ------------------------------------------------------------------ o que falta para rodar
// Cada passo aponta para a aba da configuração onde se resolve. Estados: ok, falta (ainda não preenchido), erro, neutro.
function passosSiape(s, tabelas) {
  const comTelefone = tabelas.filter((t) => t.hasPhone).length;
  return [
    { aba: 'ligacao', titulo: 'Ligação com o SQL', estado: s.sqlError ? 'erro' : s.tables ? 'ok' : 'falta',
      frase: s.sqlError || (s.tables ? `${s.tables.server || s.server}: ${plural(tabelas.length, 'tabela', 'tabelas')}, ${plural(comTelefone, 'com telefone', 'com telefone')}.` : 'Preencha servidor, banco, usuário e senha.') },
    { aba: 'bases', titulo: 'Base do mês', estado: s.chosen ? 'ok' : s.tables ? 'erro' : 'falta',
      frase: s.chosen || (s.tables ? 'Nenhuma base com telefone foi encontrada no banco.' : 'Depende da ligação com o SQL.') },
    { aba: 'saida', titulo: 'Linha de WhatsApp do CRM', estado: s.botId ? 'ok' : 'falta',
      frase: s.botId ? `Sai pela linha ${s.botId}${s.attendant ? `, escrevendo como ${s.attendant}` : ''}.` : s.crmError || 'Escolha por qual WhatsApp a mensagem sai.' },
    { aba: 'saida', titulo: 'Busca mensal automática', estado: s.enabled ? 'ok' : 'neutro',
      frase: s.enabled ? `A partir do dia ${s.day}, ${s.autoSend ? 'com envio direto, sem aprovação' : 'com aprovação antes de enviar'}.` : 'Desligada: só roda quando alguém clica em Buscar agora.' },
  ];
}

function painelRodadaSiape(s, tabelas) {
  const m = s.month, cheio = s.max ? Math.min(100, Math.round((m.created / s.max) * 100)) : 0;
  const [quandoRodou, , , nota] = (s.lastRun || '').split('|');
  const passos = passosSiape(s, tabelas);
  const pronto = passos.slice(0, 3).every((p) => p.estado === 'ok');

  const agora = botao('Buscar agora', 'lupa', 'btn mini');
  agora.title = 'Lê a base do mês agora e escreve as mensagens (até o máximo por mês). Cada cliente usa a IA.';
  agora.onclick = async () => {
    if (!s.configured) { recado('Preencha e salve a ligação com o SQL antes.', true); abrirAbaSiape('ligacao', true); return; }
    if (!s.botId) { recado('Escolha a linha de WhatsApp do CRM antes de buscar.', true); abrirAbaSiape('saida', true); return; }
    if (!confirm(`Ler a base do SIAPE agora e escrever até ${mil(s.max)} mensagens?${s.autoSend ? '\n\nATENÇÃO: o envio direto está ligado. As mensagens vão para os clientes assim que a IA escrever.' : '\n\nAs mensagens vão esperar aprovação em Mensagens para aprovar.'}`)) return;
    agora.disabled = true; agora.replaceChildren('Buscando… pode levar alguns minutos');
    try { const r = await api('POST', '/neural/siape/scan'); recado(r.message); }
    catch (e) { recado(e.message, true); }
    finally { agora.disabled = false; agora.replaceChildren(ic('lupa', 16), 'Buscar agora'); await carregarSiape(); await carregarCrm(); }
  };
  const irAprovar = botao(m.pending ? `Aprovar as ${mil(m.pending)} mensagens` : 'Abrir Mensagens para aprovar', 'balao', m.pending ? 'btn mini' : 'btn claro mini');
  irAprovar.onclick = () => ir('aprovar');

  const fatia = (n, rotulo, cor) => el('span', { style: `--c:${cor}` }, el('b', { textContent: mil(n) }), rotulo);
  const lista = el('ol', { className: 'siape-passos' }, ...passos.map((p, i) => {
    const b = el('button', { type: 'button', className: `siape-passo ${p.estado}`, title: 'Abrir na configuração' },
      el('i', { textContent: String(i + 1) }), el('div', {}, el('b', { textContent: p.titulo }), el('small', { textContent: p.frase })));
    b.onclick = () => abrirAbaSiape(p.aba, true);
    return el('li', {}, b);
  }));

  return el('div', { className: 'cartao siape-painel' },
    el('div', { className: 'siape-painel-mes' },
      el('div', { className: 'siape-painel-topo' },
        el('div', {}, el('h2', { textContent: 'Rodada do mês' }), el('p', { className: 'siape-mes', textContent: mesPorExtenso() })),
        selo(s.enabled ? 'active' : 'disabled', s.enabled ? 'Automática ligada' : 'Automática desligada')),
      el('div', { className: 'siape-contagem' }, el('b', { textContent: mil(m.created) }), el('span', { textContent: `de ${mil(s.max)} mensagens que o mês pode gastar` })),
      el('div', { className: `medidor${cheio >= 100 ? ' cheio' : ''}`, style: `--p:${cheio}%` }),
      el('div', { className: 'siape-fatias' },
        fatia(m.pending, 'esperando aprovação', 'var(--ambar)'), fatia(m.sent, 'enviadas', 'var(--verde)'), fatia(m.rejected, 'recusadas', 'var(--rubi)'),
        fatia(s.total.sent, 'enviadas desde o início', 'var(--apagado)')),
      el('div', { className: 'botoes', style: 'margin-top:6px' }, m.pending ? irAprovar : agora, m.pending ? agora : irAprovar),
      el('p', { className: 'nota', textContent: quandoRodou ? `Última busca ${quando(quandoRodou)}: ${nota}` : s.lastMonth ? `A busca automática já rodou em ${s.lastMonth.split('-').reverse().join('/')}.` : 'A busca ainda não rodou em nenhum mês.' })),
    el('div', { className: 'siape-painel-passos' },
      titulinho(pronto ? 'Tudo pronto para rodar' : 'O que falta para rodar'),
      lista));
}

// ------------------------------------------------------------------ configuração em abas
const ABAS_SIAPE = [['ligacao', 'Ligação com o SQL'], ['bases', 'Bases e regras'], ['saida', 'Saída pelo CRM']];

function abrirAbaSiape(id, rolar = false) {
  E.siapeAba = id;
  for (const b of document.querySelectorAll('.siape-abas [role="tab"]')) b.setAttribute('aria-selected', String(b.dataset.aba === id));
  for (const p of document.querySelectorAll('.siape-painel-aba')) p.hidden = p.dataset.aba !== id;
  if (rolar && $('siape-config')) $('siape-config').scrollIntoView({ behavior: 'smooth', block: 'start' });
}

const campoRotulo = (rotulo, controle, dica) => el('label', { className: 'siape-campo' }, el('span', { textContent: rotulo }), controle, dica ? el('small', { textContent: dica }) : null);

// Junta tudo o que está nas três abas e salva de uma vez (os campos ficam no DOM mesmo escondidos; são lidos na hora de salvar).
async function salvarSiape(alvo, extra = {}) {
  const v = (id) => $(id) ? $(id).value : undefined;
  const corpo = {
    server: v('siape-servidor'), database: v('siape-banco'), user: v('siape-usuario'), password: v('siape-senha'),
    table: v('siape-tabela'),
    minLiberado: Number(v('siape-liberado')), max: Number(v('siape-max')), day: Number(v('siape-dia')),
    botId: v('siape-linha'), attendant: v('siape-atendente'),
    enabled: $('siape-ligada') ? $('siape-ligada').getAttribute('aria-checked') === 'true' : E.siape.enabled,
    autoSend: $('siape-direto') ? $('siape-direto').getAttribute('aria-checked') === 'true' : E.siape.autoSend,
    ...extra,
  };
  if (alvo) alvo.disabled = true;
  try { E.siape = await api('PUT', '/neural/siape/settings', corpo); recado('Margem liberada do SIAPE salva.'); desenharSiape(); return true; }
  catch (e) { recado(e.message, true); return false; }
  finally { if (alvo && alvo.isConnected) alvo.disabled = false; }
}

function cartaoConfiguracaoSiape(s, tabelas) {
  const abas = el('div', { className: 'siape-abas', role: 'tablist' }, ...ABAS_SIAPE.map(([id, rotulo]) => {
    const b = el('button', { type: 'button', role: 'tab', 'data-aba': id, 'aria-selected': String(E.siapeAba === id), textContent: rotulo });
    b.onclick = () => abrirAbaSiape(id);
    return b;
  }));
  const paineis = { ligacao: painelLigacaoSiape(s), bases: painelBasesSiape(s, tabelas), saida: painelSaidaSiape(s) };
  for (const [id, p] of Object.entries(paineis)) { p.dataset.aba = id; p.hidden = E.siapeAba !== id; }
  const salvar = botao('Salvar alterações', null, 'btn mini'); salvar.onclick = () => salvarSiape(salvar);
  return el('div', { className: 'cartao', id: 'siape-config' },
    el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Configuração' }),
      el('p', { textContent: 'Ligação com o banco, quais bases comparar, quem entra e por onde a mensagem sai.' }))),
    abas, ...Object.values(paineis),
    el('div', { className: 'siape-rodape' }, el('span', { className: 'nota', textContent: 'Um só Salvar vale para as três abas.' }), salvar));
}

function painelLigacaoSiape(s) {
  const servidor = el('input', { className: 'campo', id: 'siape-servidor', value: s.server, autocomplete: 'off', placeholder: '192.168.0.160,1433' });
  const banco = el('input', { className: 'campo', id: 'siape-banco', value: s.database, autocomplete: 'off', placeholder: 'SIAPE' });
  const usuario = el('input', { className: 'campo', id: 'siape-usuario', value: s.user, autocomplete: 'off', placeholder: 'pontual_acesso' });
  const senha = el('input', { className: 'campo', id: 'siape-senha', type: 'password', autocomplete: 'new-password', placeholder: s.hasPassword ? 'Guardada. Preencha só para trocar' : 'Senha do login SQL' });
  const testar = botao('Salvar e testar a ligação', 'elo');
  testar.onclick = async () => {
    if (!await salvarSiape(testar)) return;
    try { const r = await api('POST', '/neural/siape/test'); recado(r.message); } catch (e) { recado(e.message, true); }
    await carregarSiape();
  };
  return el('div', { className: 'cartao-corpo siape-painel-aba' },
    el('div', { className: 'siape-cabeca' },
      el('p', { textContent: 'O banco de margens fica no SQL Server do computador da Kamila. A senha fica cifrada nesta Central e nunca aparece de novo.' }),
      selo(s.sqlError ? 'error' : s.tables ? 'active' : 'disabled', s.sqlError ? 'Sem resposta' : s.tables ? 'Conectado' : 'Não configurado')),
    el('div', { className: 'siape-grade' },
      campoRotulo('Servidor (IP,porta)', servidor, 'Use o IP e a porta: o nome da instância não resolve pela rede.'), campoRotulo('Banco', banco),
      campoRotulo('Usuário SQL', usuario), campoRotulo('Senha', senha)),
    s.sqlError ? el('p', { className: 'erro', role: 'alert', textContent: s.sqlError }) : null,
    el('div', { className: 'botoes' }, testar));
}

function painelBasesSiape(s, tabelas) {
  const opcao = (valor, rotulo, marcado) => el('option', { value: valor, textContent: rotulo, selected: marcado });
  const nomeTabela = (t) => `${t.name}${t.month ? ` · ${t.month}` : ''}`;
  const tabela = el('select', { className: 'campo', id: 'siape-tabela' },
    opcao('', 'Automático: a base mais nova com telefone', s.table === ''),
    ...tabelas.filter((t) => t.hasPhone).map((t) => opcao(t.name, nomeTabela(t), s.table === t.name)));
  if (s.table && !tabelas.some((t) => t.name === s.table)) tabela.append(opcao(s.table, `${s.table} (não está no banco)`, true));
  const liberado = el('input', { className: 'campo', id: 'siape-liberado', type: 'number', min: '0', step: '100', value: s.minLiberado });
  const max = el('input', { className: 'campo', id: 'siape-max', type: 'number', min: '1', max: '500', value: s.max });
  const dia = el('input', { className: 'campo', id: 'siape-dia', type: 'number', min: '1', max: '28', value: s.day });
  return el('div', { className: 'cartao-corpo siape-painel-aba' },
    el('div', { className: 'siape-grupo' }, titulinho('Qual base ler'),
      el('p', { className: 'siape-explica', textContent: s.chosen || 'A Central escolhe sozinha a base mais nova que tem telefone, valor liberado e positivação. Dá para escolher à mão.' }),
      el('div', { className: 'siape-grade' }, campoRotulo('Base do mês', tabela))),
    el('div', { className: 'siape-grupo' }, titulinho('Quem entra'),
      el('p', { className: 'siape-explica', textContent: 'Entra quem está com "NÃO" na positivação e tem valor liberado total (coluna TOTAL_LIBERADO) acima do mínimo. Um por CPF, do maior valor para o menor, só celulares, e ninguém recebe duas ofertas em cinco meses.' }),
      el('div', { className: 'siape-grade' },
        campoRotulo('Valor liberado mínimo (R$)', liberado, 'Pedido da chefia: só quem tem mais de R$ 1.000 liberado.'))),
    el('div', { className: 'siape-grupo' }, titulinho('Quanto e quando'),
      el('div', { className: 'siape-grade' },
        campoRotulo('Máximo de mensagens por mês', max, 'Segura o gasto da IA e protege a linha de WhatsApp.'),
        campoRotulo('A partir do dia', dia, 'A busca roda uma vez por mês, das 8h às 18h, de segunda a sábado.'))));
}

function painelSaidaSiape(s) {
  const opcao = (valor, rotulo, marcado) => el('option', { value: valor, textContent: rotulo, selected: marcado });
  const linha = el('select', { className: 'campo', id: 'siape-linha' }, opcao('', s.lines.length ? '— escolha a linha —' : 'Nenhuma linha lida do CRM', s.botId === ''),
    ...s.lines.map((l) => opcao(l.id, `${l.id}${l.funnel ? ` (funil ${l.funnel})` : ''}`, s.botId === l.id)));
  if (s.botId && !s.lines.some((l) => l.id === s.botId)) linha.append(opcao(s.botId, `${s.botId} (não está no CRM agora)`, true));
  const atendente = el('input', { className: 'campo', id: 'siape-atendente', value: s.attendant, autocomplete: 'off', placeholder: 'Ex.: Kamila. Vazio = jeito geral da equipe' });

  const chave = (id, ligado, rotulo) => el('button', { type: 'button', id, className: 'interruptor', role: 'switch', 'aria-checked': String(Boolean(ligado)), 'aria-label': rotulo });
  const ligada = chave('siape-ligada', s.enabled, 'Busca mensal automática');
  ligada.onclick = () => ligada.setAttribute('aria-checked', String(ligada.getAttribute('aria-checked') !== 'true'));
  const direto = chave('siape-direto', s.autoSend, 'Enviar sem aprovação');
  const avisoDireto = el('p', { className: 'aviso', hidden: !s.autoSend, textContent: 'Com o envio direto ligado, a mensagem vai ao cliente assim que a IA escreve, sem ninguém conferir. Comece com a aprovação ligada até confiar no texto.' });
  direto.onclick = () => { const agora = direto.getAttribute('aria-checked') !== 'true'; direto.setAttribute('aria-checked', String(agora)); avisoDireto.hidden = !agora; };

  return el('div', { className: 'cartao-corpo siape-painel-aba' },
    el('div', { className: 'siape-grupo' }, titulinho('Por onde a mensagem sai'),
      s.crmError ? el('p', { className: 'aviso', textContent: s.crmError }) : null,
      el('div', { className: 'siape-grade' },
        campoRotulo('Linha de WhatsApp do CRM', linha, 'A mensagem sai por essa linha, em nome do usuário da IA.'),
        campoRotulo('Escrever como a atendente', atendente, 'A IA imita o jeito dela nas respostas aprovadas.'))),
    el('div', { className: 'siape-grupo' }, titulinho('Como a rodada acontece'),
      el('div', { className: 'siape-opcoes' },
        el('div', { className: 'linha-opcao' }, el('div', {}, el('b', { textContent: 'Busca mensal automática' }), el('small', { textContent: 'Ligada, roda sozinha todo mês a partir do dia escolhido.' })), ligada),
        el('div', { className: 'linha-opcao' }, el('div', {}, el('b', { textContent: 'Enviar sem aprovação' }), el('small', { textContent: 'Desligado, cada mensagem espera em Mensagens para aprovar.' })), direto)),
      avisoDireto));
}

// ------------------------------------------------------------------ coluna da direita
function cartaoComoFuncionaSiape() {
  return manterAberto(el('details', { className: 'cartao apr-como' },
    el('summary', {}, ic('aprender', 16), 'Como funciona'),
    el('div', { className: 'cartao-corpo' },
      el('ul', { className: 'pontos' },
        el('li', { textContent: 'A Kamila sobe a base nova no SQL todo mês. A Central escolhe sozinha a tabela mais nova que tem telefone, valor liberado e positivação (dá para escolher à mão).' }),
        el('li', { textContent: 'Entra quem está com "NÃO" na positivação e tem valor liberado total acima do mínimo (R$ 1.000). Só celulares; um por CPF; do maior valor para o menor; quem já recebeu oferta nos últimos cinco meses fica de fora.' }),
        el('li', { textContent: 'A IA escreve a primeira mensagem como a atendente: se apresenta pela Pontual, conta que ele está com valor liberado (margem liberada) e convida para uma simulação. Nunca inventa parcela ou taxa.' }),
        el('li', { textContent: 'A mensagem sai pela tela de conversa do CRM, na linha escolhida, e o cliente ganha a etiqueta "Atendida pela IA". Quando ele responder, a conversa segue no CRM como as outras.' })))), 'siape-como');
}

function cartaoMensagensSiape(s) {
  const lista = s.recent || [];
  const pendentes = lista.filter((r) => r.status === 'pending' || r.status === 'failed');
  const tratadas = lista.filter((r) => r.status !== 'pending' && r.status !== 'failed');
  const ver = pendentes.length ? botao('Ver na fila', 'balao') : null;
  if (ver) ver.onclick = () => ir('aprovar');
  return el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Últimas mensagens de margem' }),
      el('p', { textContent: pendentes.length ? `${plural(pendentes.length, 'mensagem espera', 'mensagens esperam')} aprovação na aba Mensagens para aprovar.` : 'O que a IA escreveu para os servidores com margem nova.' })), ver),
    el('div', { className: 'cartao-corpo siape-lista' },
      ...(tratadas.length ? tratadas.slice(0, 20).map((r) => cartaoCrm(r, false))
        : [vazio('Nenhuma mensagem tratada ainda', 'As mensagens aprovadas ou recusadas aparecem aqui, com o texto que a IA escreveu.')])));
}
