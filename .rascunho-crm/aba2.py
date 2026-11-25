import io
def edit(p, pairs):
    s=io.open(p,encoding='utf-8').read()
    for a,b in pairs:
        assert s.count(a)==1,(p,a)
        s=s.replace(a,b)
    io.open(p,'w',encoding='utf-8',newline='').write(s)

edit('index.html',[
('''      <button type="button" data-ir="roteiros">''','''      <button type="button" data-ir="aprovar"><i data-ic="balao"></i><span>Mensagens para aprovar</span><em class="alerta" id="n-aprovar" hidden></em></button>
      <button type="button" data-ir="roteiros">'''),
('''<span>Clientes do CRM</span><em class="alerta" id="n-crm" hidden></em>''','''<span>Clientes do CRM</span>'''),
('''    <section class="vista rolagem" data-vista="crm" hidden>''','''    <section class="vista rolagem" data-vista="aprovar" hidden><div class="conteudo" id="v-aprovar"></div></section>
    <section class="vista rolagem" data-vista="crm" hidden>'''),
])

edit('neural.js',[
('''  relogio: '<circle''','''  balao: '<path d="M4 5h16v11H9l-5 4V5Z"/><path d="m9 10.5 2 2 4-4"/>',
  relogio: '<circle'''),
('''  crm: ['Clientes do CRM',''','''  aprovar: ['Mensagens para aprovar', 'As respostas que a IA escreveu para os clientes do CRM, separadas por atendente. Tudo o que você aprova vira exemplo para a IA.'],
  crm: ['Clientes do CRM','''),
('''  else if (E.vista === 'crm') desenharCrm();''','''  else if (E.vista === 'crm') desenharCrm();
  else if (E.vista === 'aprovar') desenharAprovar();'''),
('''  $('n-crm').textContent = mil(n); $('n-crm').hidden = !n;''','''  $('n-aprovar').textContent = mil(n); $('n-aprovar').hidden = !n;'''),
('''  if (E.vista === 'crm' && !(ativo && $('v-crm').contains(ativo) && ['TEXTAREA', 'INPUT', 'SELECT'].includes(ativo.tagName))) desenharCrm();''',
'''  const editando = (id) => ativo && $(id).contains(ativo) && ['TEXTAREA', 'INPUT', 'SELECT'].includes(ativo.tagName);
  if (E.vista === 'crm' && !editando('v-crm')) desenharCrm();
  if (E.vista === 'aprovar' && !editando('v-aprovar')) desenharAprovar();'''),
('''  $('v-crm').replaceChildren(...[
    cartaoLigacaoCrm(c),
    el('div', { className: 'cartao' },''','''  const irAprovar = botao(pend.length ? `Abrir as ${mil(pend.length)} mensagens para aprovar` : 'Abrir Mensagens para aprovar', null, 'btn mini');
  irAprovar.onclick = () => ir('aprovar');
  $('v-crm').replaceChildren(...[
    cartaoLigacaoCrm(c),
    el('div', { className: 'cartao' },
      el('h2', { textContent: 'Respostas esperando aprovação' }),
      el('p', { className: 'nota', textContent: 'Agora ficam na aba Mensagens para aprovar, separadas por atendente.' }),
      el('div', { className: 'botoes' }, irAprovar)),
    recentes.length ? el('div', { className: 'cartao' }, el('h2', { textContent: 'Últimas tratadas' }), ...recentes.map((r) => cartaoCrm(r, false))) : null,
    el('details', { className: 'cartao' }, el('summary', { textContent: `Últimos avisos recebidos do CRM (${c.events.length})` }),
      el('p', { className: 'nota', textContent: 'O que o CRM mandou, do jeito que chegou. Serve para conferir se a ligação está funcionando.' }),
      ...(c.events.length ? c.events.map(cartaoAvisoCrm) : [el('p', { className: 'nota', textContent: 'Nenhum aviso recebido ainda.' })]))].filter(Boolean));
}

// ================================================== mensagens para aprovar
// Só as respostas da IA para os clientes do CRM, agrupadas pela atendente dona do cliente. O que é aprovado
// e enviado volta para a IA como exemplo (o servidor manda as últimas aprovadas, primeiro as da mesma atendente).
const SEM_ATENDENTE = 'Sem atendente definida';
E.aprovarFiltro = '';

function desenharAprovar() {
  const c = E.crm;
  if (!c) { $('v-aprovar').replaceChildren(el('p', { className: 'nota', textContent: 'Carregando…' })); carregarCrm(); return; }
  const nomeDe = (r) => r.attendant || SEM_ATENDENTE;
  const grupos = new Map();
  for (const r of c.pending) { const n = nomeDe(r); if (!grupos.has(n)) grupos.set(n, []); grupos.get(n).push(r); }
  const nomes = [...grupos.keys()].sort((a, b) => (a === SEM_ATENDENTE) - (b === SEM_ATENDENTE) || grupos.get(b).length - grupos.get(a).length || a.localeCompare(b));
  if (E.aprovarFiltro && !grupos.has(E.aprovarFiltro)) E.aprovarFiltro = '';
  const aprendeu = new Map((c.learned || []).map((l) => [l.attendant || SEM_ATENDENTE, l]));
  const totalAprendido = (c.learned || []).reduce((s, l) => s + l.approved, 0);

  const filtro = el('div', { className: 'segmento', role: 'group', 'aria-label': 'Mostrar as mensagens de qual atendente', style: 'flex-wrap:wrap' },
    ...[['', `Todas (${mil(c.pending.length)})`], ...nomes.map((n) => [n, `${n} (${mil(grupos.get(n).length)})`])].map(([v, rotulo]) => {
      const b = el('button', { type: 'button', textContent: rotulo, 'aria-pressed': String(E.aprovarFiltro === v) });
      b.onclick = () => { E.aprovarFiltro = v; desenharAprovar(); };
      return b;
    }));

  const mostrar = E.aprovarFiltro ? [E.aprovarFiltro] : nomes;
  const blocos = mostrar.map((n) => {
    const l = aprendeu.get(n);
    const dela = n === SEM_ATENDENTE ? 'sem atendente' : `de ${n}`;
    const aprendido = l
      ? `A IA já aprendeu com ${plural(l.approved, 'resposta aprovada', 'respostas aprovadas')} ${dela}${l.edited ? `, ${mil(l.edited)} delas corrigida(s) por você` : ''}.`
      : 'Ainda nenhuma resposta aprovada para esta atendente: a IA usa o jeito geral da equipe.';
    return el('div', { className: 'cartao' },
      el('div', { className: 'cartao-topo' }, el('div', {},
        el('h2', { textContent: n }),
        el('p', { textContent: `${plural(grupos.get(n).length, 'mensagem esperando aprovação', 'mensagens esperando aprovação')}. ${aprendido}` }))),
      ...grupos.get(n).map((r) => cartaoCrm(r, true)));
  });

  $('v-aprovar').replaceChildren(...[
    el('div', { className: 'cartao' },
      el('div', { className: 'cartao-topo' }, el('div', {},
        el('h2', { textContent: 'Como funciona' }),
        el('p', { textContent: 'A IA escreve a resposta no jeito da atendente de quem é o cliente. Confira, corrija se precisar e aprove: só então a mensagem vai para o cliente, pelo CRM, e ele ganha a etiqueta "Atendida pela IA". Cada resposta aprovada vira exemplo para as próximas; quando você corrige o texto, a IA aprende o jeito certo.' }))),
      el('p', { className: 'nota', textContent: totalAprendido ? `Até agora a IA aprendeu com ${plural(totalAprendido, 'resposta aprovada', 'respostas aprovadas')}.` : 'Ainda nenhuma resposta aprovada: a primeira já começa a ensinar.' }),
      c.enabled ? null : el('p', { className: 'aviso', textContent: 'A IA está desligada para o CRM. Ligue em Clientes do CRM para ela voltar a escrever.' })),
    c.pending.length ? el('div', { className: 'cartao' }, filtro) : null,
    ...(c.pending.length ? blocos : [el('div', { className: 'cartao' }, vazio('Nenhuma mensagem esperando', 'Quando a IA escrever uma resposta para um cliente do CRM, ela aparece aqui, na atendente dona do cliente.'))]),
  ].filter(Boolean));
}

function desenharCrmAntigo() {
  const c = E.crm, pend = c.pending, recentes = c.recent;
  $('v-crm').replaceChildren(...[
    el('div', { className: 'cartao' },'''),
('''    el('header', {}, selo(classe, rotulo), el('b', { textContent: nome }),''','''    el('header', {}, selo(classe, rotulo), el('b', { textContent: nome }), r.attendant ? el('small', { textContent: `Atendente: ${r.attendant}` }) : null,'''),
('''  } else if (r.reply) {
    partes.push(el('div', { className: 'citacao' }, el('b', { textContent: r.status === 'sent' || r.status === 'human' ? 'Enviado' : 'Resposta escrita' }), r.reply));''','''  } else if (r.reply) {
    partes.push(el('div', { className: 'citacao' }, el('b', { textContent: r.status === 'sent' || r.status === 'human' ? 'Enviado' : 'Resposta escrita' }), r.reply));
    if (r.draft && r.draft.trim() !== r.reply.trim()) partes.push(el('details', {}, el('summary', { textContent: 'O que a IA tinha escrito (corrigido antes de enviar)' }), el('div', { className: 'citacao', style: 'white-space:pre-wrap', textContent: r.draft })));'''),
])
