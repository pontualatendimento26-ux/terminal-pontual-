// Central Neural — aba Mensagens para aprovar. Fila de respostas da IA por atendente (lista à esquerda, conversas
// à direita), cartão de conversa em balões com a resposta editável, e a busca de clientes perdidos num painel
// compacto. Redefine desenharAprovar, cartaoPerdidos e cartaoCrm de neural.js (carregado antes) e usa o mesmo
// estado (E.crm, E.crmTextos, E.crmPara, E.abertos, E.aprovarFiltro). Mesmas regras: nada do servidor entra como HTML.
'use strict';

const CORES_PESSOA = ['#4fd8ff', '#9b8cff', '#3ddc97', '#ffb454', '#ff8fb8', '#5b8cff', '#ffe08f', '#7be0d6'];
function corPessoa(nome) { let h = 0; for (const c of semAcento(nome)) h = (h * 31 + c.charCodeAt(0)) >>> 0; return CORES_PESSOA[h % CORES_PESSOA.length]; }
function iniciais(nome) {
  const p = texto(nome).trim().split(/\s+/).filter((x) => /^[\p{L}\p{N}]/u.test(x));
  return ((p[0]?.[0] || '') + (p.length > 1 ? p[p.length - 1][0] : '')).toUpperCase() || '#';
}
const avatar = (nome, classe = '') => el('span', { className: `avatar ${classe}`, style: `--c:${corPessoa(nome)}`, textContent: iniciais(nome), 'aria-hidden': 'true' });

// "há 3 h", "há 2 dias": quanto tempo o cliente está esperando.
function espera(iso) {
  if (!iso) return '';
  const ms = Date.now() - new Date(iso).getTime();
  if (Number.isNaN(ms) || ms < 0) return '';
  const h = Math.floor(ms / 36e5);
  if (h < 1) return 'há menos de 1 hora';
  if (h < 24) return `há ${h} h`;
  return `há ${plural(Math.floor(h / 24), 'dia', 'dias')}`;
}

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
  const totalCorrigido = (c.learned || []).reduce((s, l) => s + (l.edited || 0), 0);
  const perdidos = c.pending.filter((r) => r.kind === 'perdido').length;
  const margens = c.pending.filter((r) => r.kind === 'margem').length;

  const numeros = el('div', { className: 'numeros' },
    tile(c.pending.length, 'Esperando aprovação', c.pending.length ? 'Confira, corrija e aprove para enviar' : 'Nada na fila agora', c.pending.length > 0),
    tile(perdidos, 'Clientes perdidos na fila', 'Retomadas escritas pela IA'),
    tile(margens, 'Valor liberado na fila', 'Primeiras mensagens do SIAPE'),
    tile(totalAprendido, 'Respostas aprovadas', totalAprendido ? 'Cada uma vira exemplo para a IA' : 'A primeira já começa a ensinar'),
    tile(totalCorrigido, 'Corrigidas por você', 'A IA aprende o jeito certo'));

  const mostrar = E.aprovarFiltro ? [E.aprovarFiltro] : nomes;
  const lista = mostrar.flatMap((n) => grupos.get(n));
  $('v-aprovar').replaceChildren(
    numeros,
    c.enabled ? null : el('p', { className: 'aviso', textContent: 'A IA está desligada para o CRM. Ligue em Clientes do CRM para ela voltar a escrever.' }),
    el('div', { className: 'apr-layout' },
      el('div', { className: 'apr-lateral' }, cartaoAtendentes(c, nomes, grupos, aprendeu), cartaoPerdidos(c), cartaoEtiquetas(c), cartaoComoFunciona()),
      carrosselConversas(lista, E.aprovarFiltro || 'Todas as atendentes', aprendeu.get(E.aprovarFiltro))));
}

// Caixa com uma conversa por vez: a rolagem encaixa de cartão em cartão (scroll snap), com setas e contador.
// A conversa atual fica guardada em E.carrosselId para a tela se redesenhar (a cada 10 s) sem pular de conversa.
// opcoes (para reaproveitar na tela Clientes do CRM): editavel, chave (onde guardar a conversa atual), sub, vazio.
function carrosselConversas(lista, titulo, aprendido, opcoes = {}) {
  const editavel = opcoes.editavel ?? true, chave = opcoes.chave || 'carrosselId';
  const caixa = el('div', { className: 'apr-carrossel', tabindex: '0', role: 'region', 'aria-label': editavel ? 'Conversas esperando aprovação' : 'Conversas já tratadas' });
  if (!lista.length) caixa.append(el('div', { className: 'msg' }, vazio(...(opcoes.vazio || ['Nenhuma mensagem esperando', 'Quando a IA escrever uma resposta para um cliente do CRM, ela aparece aqui, na atendente dona do cliente.']))));
  for (const r of lista) caixa.append(cartaoCrm(r, editavel));
  const contador = el('b', { className: 'apr-contador', 'aria-live': 'polite' });
  const cima = iconeBotao('recolher', 'Conversa anterior'), baixo = iconeBotao('expandir', 'Próxima conversa');
  const cartoes = () => [...caixa.querySelectorAll(':scope > .msg')];
  const atual = () => { const t = caixa.scrollTop + 20; /* 12 px da margem de encaixe + folga */ const c = cartoes(); let i = 0; c.forEach((m, k) => { if (m.offsetTop <= t) i = k; }); return i; };
  // Dois cliques seguidos: o segundo parte do destino do primeiro, não de onde a animação estava. Durante a
  // animação o encaixe é desligado (com ele ligado o navegador pararia no primeiro cartão do caminho).
  let destino = null, destinoTopo = 0, fimRolagem = null;
  const irPara = (i) => { const c = cartoes(); if (!c.length) return; i = Math.max(0, Math.min(c.length - 1, i)); destino = i; destinoTopo = c[i].offsetTop - 12; caixa.style.scrollSnapType = 'none'; caixa.scrollTo({ top: destinoTopo, behavior: 'smooth' }); pintar(); };
  const pintar = () => { const n = lista.length, i = destino ?? atual(); contador.textContent = n ? `${mil(i + 1)} de ${mil(n)}` : '0 de 0'; cima.disabled = i <= 0; baixo.disabled = i >= n - 1; };
  cima.onclick = () => irPara((destino ?? atual()) - 1); baixo.onclick = () => irPara((destino ?? atual()) + 1);
  // Enquanto a tela restaura a posição (redesenho de 10 s) nada é gravado: só a rolagem da pessoa conta.
  let restaurando = false;
  caixa.addEventListener('scroll', () => { if (restaurando) return; clearTimeout(fimRolagem); fimRolagem = setTimeout(() => {
      // A animação parou antes do destino (aba em segundo plano, por exemplo): termina o caminho de uma vez.
      if (destino !== null && Math.abs(caixa.scrollTop - destinoTopo) > 2 && caixa.scrollTop + caixa.clientHeight < caixa.scrollHeight - 2) { caixa.scrollTo({ top: destinoTopo, behavior: 'instant' }); return; }
      destino = null; caixa.style.scrollSnapType = ''; pintar();
      E[chave] = lista[atual()]?.id ?? null; /* guarda a conversa, não os pixels: o redesenho volta para ela */
    }, 160); if (destino === null) pintar(); }, { passive: true });
  // A pessoa mexeu (roda, dedo, barra): a animação das setas deixa de valer e nada a puxa de volta.
  for (const ev of ['wheel', 'touchstart', 'pointerdown']) caixa.addEventListener(ev, () => { destino = null; caixa.style.scrollSnapType = ''; }, { passive: true });
  caixa.addEventListener('keydown', (e) => { if (e.target !== caixa) return; if (e.key === 'ArrowDown' || e.key === 'PageDown') { e.preventDefault(); irPara((destino ?? atual()) + 1); } if (e.key === 'ArrowUp' || e.key === 'PageUp') { e.preventDefault(); irPara((destino ?? atual()) - 1); } });
  const sub = opcoes.sub ? opcoes.sub : aprendido ? `A IA já aprendeu com ${plural(aprendido.approved, 'resposta aprovada', 'respostas aprovadas')} dela.` : titulo === 'Todas as atendentes' ? 'Role para passar de conversa em conversa.' : 'Ainda sem resposta aprovada desta atendente.';
  const topo = el('div', { className: 'apr-carrossel-topo' },
    titulo === 'Todas as atendentes' ? el('span', { className: 'avatar todos', 'aria-hidden': 'true' }, ic('balao', 16)) : avatar(titulo),
    el('div', {}, el('b', { textContent: titulo }), el('small', { textContent: sub })),
    contador, cima, baixo);
  const bloco = el('div', { className: 'cartao apr-caixa' }, topo, caixa);
  // Altura: até o fim da janela. Depois de entrar na página, volta para a conversa em que a pessoa estava.
  // A volta é instantânea, sem encaixe e sem animação: se não, a caixa "rolava sozinha" a cada redesenho.
  requestAnimationFrame(() => {
    const alto = Math.max(480, window.innerHeight - caixa.getBoundingClientRect().top - 22);
    caixa.style.height = `${alto}px`;
    const i = Math.max(0, lista.findIndex((r) => r.id === E[chave])), c = cartoes();
    restaurando = true; caixa.style.scrollBehavior = 'auto'; caixa.style.scrollSnapType = 'none';
    caixa.scrollTop = c[i] ? c[i].offsetTop - 12 : 0;
    requestAnimationFrame(() => { caixa.style.scrollBehavior = ''; caixa.style.scrollSnapType = ''; restaurando = false; pintar(); });
  });
  return bloco;
}

// Lista de atendentes com a fila de cada uma. Clicar filtra a fila da direita.
function cartaoAtendentes(c, nomes, grupos, aprendeu) {
  const linha = (valor, nome, qtd, nota) => {
    const b = el('button', { type: 'button', className: 'apr-pessoa', 'aria-pressed': String(E.aprovarFiltro === valor) },
      valor ? avatar(nome) : el('span', { className: 'avatar todos', 'aria-hidden': 'true' }, ic('balao', 16)),
      el('div', {}, el('b', { textContent: nome }), el('small', { textContent: nota })),
      el('em', { className: qtd ? 'quente' : '', textContent: mil(qtd) }));
    b.onclick = () => { E.aprovarFiltro = valor; desenharAprovar(); };
    return b;
  };
  const itens = [linha('', 'Todas as atendentes', c.pending.length, `${plural(nomes.length, 'atendente com fila', 'atendentes com fila')}`)];
  for (const n of nomes) {
    const l = aprendeu.get(n);
    itens.push(linha(n, n, grupos.get(n).length, l ? `${plural(l.approved, 'aprovada', 'aprovadas')} até agora` : 'ainda sem aprovadas'));
  }
  return el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Fila por atendente' }), el('p', { textContent: 'A IA escreve no jeito da dona do cliente.' }))),
    el('div', { className: 'apr-pessoas' }, ...itens));
}

function cartaoComoFunciona() {
  return manterAberto(el('details', { className: 'cartao apr-como' },
    el('summary', {}, ic('aprender', 16), 'Como funciona'),
    el('div', { className: 'cartao-corpo' },
      el('ul', { className: 'pontos' },
        el('li', { textContent: 'A IA escreve a resposta no jeito da atendente de quem é o cliente.' }),
        el('li', { textContent: 'Confira, corrija se precisar e aprove: só então a mensagem vai para o cliente, pelo CRM, e ele ganha a etiqueta "Atendida pela IA".' }),
        el('li', { textContent: 'Cada resposta aprovada vira exemplo para as próximas. Quando você corrige o texto, a IA aprende o jeito certo.' })))), 'aprovar-como');
}

// Busca dos clientes perdidos: no funil Atendimento Portabilidade, quem escreveu por último e está sem resposta
// há mais de um dia. Roda sozinha a cada meia hora, das 8h às 18h, de segunda a sábado, se ligada.
function cartaoPerdidos(c) {
  const [quandoRodou, , , nota] = (c.lostLastRun || '').split('|');
  const dia = (n) => { const d = new Date(); d.setDate(d.getDate() - n); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; };
  E.perdidosDe ??= c.lostPeriod?.from || dia(15); E.perdidosAte ??= c.lostPeriod?.to || dia(1);
  const de = el('input', { type: 'date', className: 'campo', value: E.perdidosDe, min: dia(60), max: dia(0), 'aria-label': 'Data de início da busca' });
  const ate = el('input', { type: 'date', className: 'campo', value: E.perdidosAte, min: dia(60), max: dia(0), 'aria-label': 'Data de fim da busca' });
  // Trocar a data já filtra a lista (espera a pessoa terminar de digitar; não busca nem gasta IA).
  const trocou = () => {
    E.perdidosDe = de.value; E.perdidosAte = ate.value;
    clearTimeout(E.perdidosEspera);
    if (!de.value || !ate.value || !de.validity.valid || !ate.validity.valid || de.value > ate.value) return;
    E.perdidosEspera = setTimeout(() => {
      if (document.activeElement === de || document.activeElement === ate) document.activeElement.blur();
      agirCrm(null, () => api('PUT', '/neural/crm/lost/period', { from: de.value, to: ate.value }), `Mostrando só os clientes de ${diaMes(de.value)} a ${diaMes(ate.value)}.`);
    }, 1200);
  };
  de.onchange = trocou; ate.onchange = trocou;

  const agora = botao('Buscar neste período', 'lupa', 'btn mini');
  agora.title = 'Procura os clientes que escreveram por último entre as duas datas e ficaram sem resposta. Cada conversa lida usa a IA (no máximo 25 por busca).';
  agora.onclick = async () => {
    if (!de.value || !ate.value) { recado('Preencha a data de início e a data de fim.', true); return; }
    clearTimeout(E.perdidosEspera);
    if (de.value > ate.value) { recado('A data de início precisa ser antes da data de fim.', true); return; }
    agora.disabled = true; agora.replaceChildren('Buscando… pode levar alguns minutos');
    try { const r = await api('POST', '/neural/crm/lost/scan', { from: de.value, to: ate.value }); recado(r.message); }
    catch (e) { recado(e.message, true); }
    finally { agora.disabled = false; agora.replaceChildren(ic('lupa', 16), 'Buscar neste período'); await carregarCrm(); }
  };

  const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(Boolean(c.lostEnabled)), 'aria-label': 'Busca automática' });
  chave.onclick = () => agirCrm(chave, () => api('PUT', '/neural/crm/lost', { enabled: !c.lostEnabled }), c.lostEnabled ? 'Busca automática desligada.' : 'Busca automática ligada: roda a cada meia hora, das 8h às 18h.');

  return el('div', { className: 'cartao apr-perdidos' },
    el('div', { className: 'cartao-topo' },
      el('div', {}, el('h2', { textContent: 'Clientes perdidos' }), el('p', { textContent: 'Quem escreveu por último e ficou sem resposta.' })),
      selo(c.lostEnabled ? 'active' : 'disabled', c.lostEnabled ? 'Automática ligada' : 'Automática desligada')),
    el('div', { className: 'cartao-corpo' },
      el('div', { className: 'apr-datas' }, el('label', {}, 'De', de), el('label', {}, 'Até', ate)),
      agora,
      avisoPeriodo(c),
      el('div', { className: 'linha-opcao' },
        el('div', {}, el('b', { textContent: 'Busca automática' }), el('small', { textContent: `A cada meia hora, das 8h às 18h, de segunda a sábado, ${c.lostPeriod ? `só no período escolhido (${diaMes(c.lostPeriod.from)} a ${diaMes(c.lostPeriod.to)})` : 'nos últimos 15 dias'} e quem espera há mais de 24 horas.` })),
        chave),
      el('p', { className: 'nota', textContent: quandoRodou ? `Última busca ${quando(quandoRodou)}: ${nota}` : 'A busca ainda não rodou.' }),
      manterAberto(el('details', { className: 'apr-como' }, el('summary', {}, 'Como a IA escolhe os clientes'),
        el('p', { className: 'nota', textContent: 'A IA procura no funil Atendimento Portabilidade os clientes que mandaram a última mensagem entre as duas datas (incluindo os dois dias) e estão sem resposta há mais de um dia. Quem escreveu há menos de 1 hora fica de fora: a atendente pode estar respondendo. Ela lê a conversa, vê quem é a responsável no CRM e escreve a retomada no jeito dela. Quem só mandou "ok" ou "obrigado" e quem já foi finalizado (Ganho ou Perdido no CRM, ou conversa encerrada) fica de fora. Se aparecer algum, clique em "Já foi finalizado": a IA aprende. Dá para olhar até 60 dias para trás.' })), 'perdidos-como')));
}

// Etiquetas por tipo de cliente: a IA lê as conversas do funil e põe uma etiqueta no CRM. Quem tem etiqueta
// não entra na fila de aprovação. Roda só pelo botão, até 60 conversas por vez.
function cartaoEtiquetas(c) {
  const [quandoRodou, estado, ...resto] = (c.tagsLastRun || '').split('|');
  const nota = resto.join('|');
  const rodando = Boolean(c.tagsRunning);
  const btn = botao(rodando ? 'Lendo as conversas…' : 'Etiquetar clientes', 'aprender', 'btn mini');
  btn.disabled = rodando;
  btn.title = `A IA lê as conversas do funil Atendimento Portabilidade e põe a etiqueta no CRM. Até ${60} conversas por clique; quem já foi lido só volta se houver mensagem nova.`;
  btn.onclick = async () => {
    btn.disabled = true;
    try { const r = await api('POST', '/neural/crm/tags/scan'); recado(r.message); }
    catch (e) { recado(e.message, true); }
    finally { await carregarCrm(); }
  };
  const contagem = new Map((c.tagCounts || []).map((t) => [t.tag, t.count]));
  return el('div', { className: 'cartao apr-etiquetas' },
    el('div', { className: 'cartao-topo' },
      el('div', {}, el('h2', { textContent: 'Etiquetas dos clientes' }), el('p', { textContent: 'A IA lê a conversa e marca o tipo de cliente no CRM.' })),
      selo(rodando ? 'pending' : 'disabled', rodando ? 'Rodando' : 'Parada')),
    el('div', { className: 'cartao-corpo' },
      btn,
      el('ul', { className: 'pontos' }, ...(c.tagNames || []).map((n) => el('li', { textContent: `${n}: ${contagem.get(n) || 0}` }))),
      el('p', { className: 'nota', textContent: quandoRodou ? `${estado === 'rodando' ? 'Começou' : 'Última rodada'} ${quando(quandoRodou)}: ${nota}` : 'Ainda não rodou.' }),
      el('p', { className: 'nota', textContent: 'Cliente com uma dessas etiquetas não vai para Mensagens para aprovar. Etiqueta posta ou trocada à mão no CRM vale: a IA não mexe.' })));
}

// Uma conversa: cliente à esquerda, resposta da IA à direita (editável enquanto espera aprovação).
function cartaoCrm(r, editavel) {
  const [rotulo, classe] = SITUACAO_CRM[r.status] || [r.status, 'liso'];
  const nome = r.contact || r.phone || `Conversa ${r.conversationId}`;
  const sub = [r.contact && r.phone ? r.phone : null, r.attendant ? `Atendente: ${r.attendant}` : null].filter(Boolean).join(' · ');
  const tempo = r.sentAt ? `enviada ${quando(r.sentAt)}` : r.waitingSince ? `sem resposta desde ${quando(r.waitingSince)}` : `escrita ${quando(r.createdAt)}`;
  const margem = r.kind === 'margem';
  const selos = el('div', { className: 'msg-selos' },
    r.kind === 'perdido' ? selo('pending', 'Cliente perdido') : margem ? selo('system', 'Valor liberado') : null,
    editavel && r.waitingSince ? selo('error liso', `esperando ${espera(r.waitingSince)}`) : selo(classe, rotulo),
    el('small', { textContent: tempo }));
  const partes = [
    el('header', { className: 'msg-topo' }, avatar(nome), el('div', { className: 'msg-quem' }, el('b', { textContent: nome }), sub ? el('small', { textContent: sub }) : null), selos),
    el('div', { className: 'msg-balao cliente' }, el('b', { textContent: margem ? 'Dados da base do SIAPE' : 'Cliente escreveu' }), el('span', { textContent: r.incoming })),
  ];
  if (editavel) {
    const campo = el('textarea', { className: 'campo', value: E.crmTextos.get(r.id) ?? r.reply, 'aria-label': `Resposta para ${nome}`, placeholder: 'Escreva a resposta para o cliente' });
    campo.oninput = () => E.crmTextos.set(r.id, campo.value);
    partes.push(el('div', { className: 'msg-balao equipe' }, el('b', { textContent: margem ? 'Primeira mensagem da IA · você pode editar' : 'Resposta da IA · você pode editar' }), campo));
    if (r.decision) partes.push(el('p', { className: `msg-decisao ${r.decision}` }, ic('raio', 15), el('span', { textContent: DECISAO_CRM[r.decision] || '' })));
    if (r.kind === 'perdido' && r.decision === 'humano') partes.push(el('p', { className: 'aviso', textContent: 'A IA acha que só a atendente pode responder este cliente. Se concordar, recuse e avise a atendente.' }));

    const aprovar = botao('Aprovar e enviar', 'enviar', 'btn mini');
    aprovar.onclick = async () => { if (await agirCrm(aprovar, () => api('POST', `/neural/crm/replies/${r.id}/approve`, { message: campo.value }), `Enviada para ${nome}.`)) E.crmTextos.delete(r.id); };
    const sugerido = r.decision === 'humano';
    const humano = botao(sugerido ? 'Passar para atendente (sugerido)' : 'Passar para atendente', null, sugerido ? 'btn mini' : 'btn claro mini');
    humano.title = 'Manda o texto da caixa ao cliente (se houver) e devolve a conversa para a atendente escolhida ao lado, no CRM.';
    // A conversa volta para a atendente que já é dona do cliente; a lista vem do CRM, com ela já escolhida.
    const para = el('select', { className: 'campo', 'aria-label': `Atendente que recebe a conversa de ${nome}` },
      el('option', { value: '', textContent: E.crmUsuarios ? '— escolha a atendente —' : 'Carregando atendentes…' }));
    const preencherPara = () => {
      if (!E.crmUsuarios) return;
      const dona = semAcento(r.attendant).trim();
      const achada = E.crmUsuarios.find((u) => semAcento(u.name).trim() === dona)
        || (dona && E.crmUsuarios.filter((u) => semAcento(u.name).trim().startsWith(`${dona} `)).length === 1 ? E.crmUsuarios.find((u) => semAcento(u.name).trim().startsWith(`${dona} `)) : null);
      const escolhido = E.crmPara.get(r.id) ?? achada?.id ?? '';
      para.replaceChildren(el('option', { value: '', textContent: '— escolha a atendente —' }),
        ...E.crmUsuarios.map((u) => el('option', { value: u.id, textContent: u.id === achada?.id ? `${u.name} (dona do cliente)` : u.name, selected: u.id === escolhido })));
    };
    para.onchange = () => E.crmPara.set(r.id, para.value);
    preencherPara();
    if (!E.crmUsuarios && r.kind !== 'perdido' && !margem) carregarUsuariosCrm().then(preencherPara);
    humano.onclick = async () => {
      if (!para.value) { recado('Escolha a atendente que recebe a conversa.', true); para.focus(); return; }
      const quem = para.selectedOptions[0].textContent.replace(' (dona do cliente)', '');
      if (!confirm(`Passar a conversa de ${nome} para ${quem} no CRM?${campo.value.trim() ? '\n\nO texto da caixa vai para o cliente antes.' : ''}`)) return;
      if (await agirCrm(humano, () => api('POST', `/neural/crm/replies/${r.id}/human`, { message: campo.value, userId: para.value }), `Conversa de ${nome} passada para ${quem}.`)) { E.crmTextos.delete(r.id); E.crmPara.delete(r.id); }
    };
    const recusar = botao('Recusar', 'x', 'btn claro mini');
    recusar.onclick = async () => { if (await agirCrm(recusar, () => api('POST', `/neural/crm/replies/${r.id}/reject`), 'Resposta recusada. Nada foi enviado.')) E.crmTextos.delete(r.id); };
    // O perdido já é da atendente no CRM: não há para quem passar. Recusar deixa com ela, sem mandar nada.
    const finalizado = botao('Já foi finalizado', null, 'btn claro mini');
    finalizado.title = 'Não envia nada, tira o cliente desta lista e ensina a IA a deixar de fora conversas parecidas.';
    finalizado.onclick = async () => { if (await agirCrm(finalizado, () => api('POST', `/neural/crm/replies/${r.id}/finished`), `${nome} marcado como já finalizado. Nada foi enviado.`)) E.crmTextos.delete(r.id); };
    const acoes = r.kind === 'perdido' ? [aprovar, finalizado, el('span', { className: 'empurra' }), recusar]
      : margem ? [aprovar, el('span', { className: 'empurra' }), recusar] : [aprovar, humano, para, el('span', { className: 'empurra' }), recusar];
    partes.push(el('footer', { className: 'msg-acoes' }, ...acoes));
  } else if (r.reply) {
    partes.push(el('div', { className: 'msg-balao equipe' }, el('b', { textContent: r.status === 'sent' || r.status === 'human' ? 'Enviado' : 'Resposta escrita' }), el('span', { textContent: r.reply })));
    if (r.draft && r.draft.trim() !== r.reply.trim()) partes.push(manterAberto(el('details', { className: 'ver-conversa' }, el('summary', { textContent: 'O que a IA tinha escrito (corrigido antes de enviar)' }), el('div', { className: 'citacao', textContent: r.draft })), `rascunho:${r.id}`));
  }
  if (r.reason) partes.push(el('p', { className: 'nota', textContent: `Motivo da IA: ${r.reason}${r.reviewedBy ? ` · ${r.reviewedBy}` : ''}` }));
  if (r.error) partes.push(el('p', { className: r.status === 'human' ? 'nota' : 'erro', textContent: r.error }));
  if (!margem) partes.push(detalhesConversa('Ver a conversa inteira', r.transcript, `conversa:${r.id}`));
  return el('article', { className: `msg${r.kind === 'perdido' ? ' perdido' : ''}${margem ? ' margem' : ''}${editavel ? '' : ' tratada'}` }, ...partes);
}

// ================================================== Clientes do CRM (refeita em 26/11/2026)
// Números em cima; à esquerda o filtro por situação, a ligação (recolhida) e os avisos do CRM;
// à direita as conversas já tratadas num carrossel vertical, uma por vez, para analisar com calma.
E.crmFiltro = '';
function desenharCrm() {
  const c = E.crm;
  if (!c) { $('v-crm').replaceChildren(el('p', { className: 'nota', textContent: 'Carregando…' })); carregarCrm(); return; }
  const recentes = c.recent || [];
  const grupos = new Map();
  for (const r of recentes) { if (!grupos.has(r.status)) grupos.set(r.status, []); grupos.get(r.status).push(r); }
  if (E.crmFiltro && !grupos.has(E.crmFiltro)) E.crmFiltro = '';
  const conta = (st) => grupos.get(st)?.length || 0;
  const corrigidas = recentes.filter((r) => r.draft && r.reply && r.draft.trim() !== r.reply.trim()).length;

  const numeros = el('div', { className: 'numeros' },
    tile(c.pending.length, 'Esperando aprovação', c.pending.length ? 'Estão na aba Mensagens para aprovar' : 'Nada na fila agora', c.pending.length > 0),
    tile(conta('sent'), 'Enviadas', 'Aprovadas e entregues pelo CRM'),
    tile(conta('human'), 'Passadas para atendente', 'Voltaram para a dona do cliente'),
    tile(conta('rejected') + conta('skipped') + conta('outdated'), 'Não enviadas', 'Recusadas, sem resposta ou fora da fila'),
    tile(corrigidas, 'Corrigidas antes de enviar', 'A IA aprende com elas'));

  // Filtro por situação (mesmo visual da fila por atendente).
  const linha = (valor, nome, qtd) => {
    const b = el('button', { type: 'button', className: 'apr-pessoa', 'aria-pressed': String(E.crmFiltro === valor) },
      el('span', { className: 'avatar todos', 'aria-hidden': 'true' }, ic('balao', 16)),
      el('div', {}, el('b', { textContent: nome }), el('small', { textContent: valor ? `situação: ${nome.toLowerCase()}` : 'tudo o que já foi tratado' })),
      el('em', { className: qtd && valor === '' ? 'quente' : '', textContent: mil(qtd) }));
    b.onclick = () => { E.crmFiltro = valor; E.crmCarrosselId = null; desenharCrm(); };
    return b;
  };
  const filtro = el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Últimas tratadas' }), el('p', { textContent: 'Escolha o que quer analisar.' }))),
    el('div', { className: 'apr-pessoas' }, linha('', 'Todas', recentes.length),
      ...[...grupos.keys()].map((st) => linha(st, (SITUACAO_CRM[st] || [st])[0], conta(st)))));

  const irAprovar = botao(c.pending.length ? `Aprovar ${mil(c.pending.length)} mensagens` : 'Abrir Mensagens para aprovar', null, 'btn mini');
  irAprovar.onclick = () => ir('aprovar');
  const estado = el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {},
      el('h2', { textContent: 'IA no CRM' }),
      el('p', { textContent: c.enabled ? 'Ligada: a IA escreve e nada vai ao cliente sem aprovação.' : 'Desligada: os avisos chegam, a IA não escreve.' })),
      selo(c.enabled ? 'active' : 'disabled', c.enabled ? 'Ligada' : 'Desligada')),
    el('div', { className: 'botoes' }, irAprovar));

  const ligacao = manterAberto(el('details', { className: 'cartao apr-como' },
    el('summary', {}, ic('aprender', 16), 'Ligação com o CRM (configuração)'),
    cartaoLigacaoCrm(c)), 'crm-ligacao');
  const avisos = manterAberto(el('details', { className: 'cartao apr-como' },
    el('summary', {}, ic('balao', 16), `Avisos recebidos do CRM (${c.events.length})`),
    el('div', { className: 'cartao-corpo', style: 'display:grid;gap:10px;max-height:420px;overflow:auto' },
      el('p', { className: 'nota', textContent: 'O que o CRM mandou, do jeito que chegou. Serve para conferir se a ligação está funcionando.' }),
      ...(c.events.length ? c.events.map(cartaoAvisoCrm) : [el('p', { className: 'nota', textContent: 'Nenhum aviso recebido ainda.' })]))), 'avisos-crm');

  const lista = E.crmFiltro ? grupos.get(E.crmFiltro) : recentes;
  const titulo = E.crmFiltro ? (SITUACAO_CRM[E.crmFiltro] || [E.crmFiltro])[0] : 'Todas as atendentes';
  $('v-crm').replaceChildren(
    numeros,
    el('div', { className: 'apr-layout' },
      el('div', { className: 'apr-lateral crm-lateral' }, estado, filtro, ligacao, avisos),
      carrosselConversas(lista, titulo, null, {
        editavel: false, chave: 'crmCarrosselId',
        sub: 'Role ou use as setas para passar de conversa em conversa.',
        vazio: ['Nenhuma conversa tratada ainda', 'Quando uma resposta for aprovada, recusada ou passada para a atendente, ela aparece aqui.'],
      })));
}
