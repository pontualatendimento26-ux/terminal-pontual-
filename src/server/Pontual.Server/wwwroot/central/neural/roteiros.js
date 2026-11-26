// Central Neural — aba Roteiros: cartões por banco e convênio, ranking de venda (o que a IA responde quando a
// atendente pergunta "qual o melhor roteiro do banco X"), filtros e convênios por UF.
// Usa as funções e o estado de neural.js (carregado antes). Mesmas regras: nada do servidor entra como HTML.
'use strict';

// Portais dos bancos: cor do cartão e onde a atendente baixa o roteiro. Banco sem entrada ganha cor da paleta.
const BANCOS = {
  daycoval: {
    cor: '#2a4fd8', luz: '#aebdff',
    portal: 'https://www.daycoval.com.br/canalcorrespondente/DocumentosRoteiros',
    login: 'https://consignado.daycoval.com.br/Autorizador/Login/AC.UI.LOGIN.aspx',
    passos: ['Entre no portal do correspondente Daycoval com o seu login.', 'Abra Documentos / Roteiros e busque UMA palavra do nome do arquivo (a cidade ou o órgão). Várias palavras devolvem lista vazia.', 'Baixe o arquivo SIMP, se houver, ou o completo, e confira a data no nome.'],
  },
};
const PALETA_BANCOS = ['#4fd8ff', '#9b8cff', '#3ddc97', '#ffb454', '#ff8fb8', '#5b8cff', '#ffe08f', '#7be0d6'];
function bancoDe(nome) {
  const chave = semAcento(nome).replace(/[^a-z0-9]/g, '');
  if (BANCOS[chave]) return BANCOS[chave];
  let h = 0; for (const c of chave) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  const cor = PALETA_BANCOS[h % PALETA_BANCOS.length];
  return { cor, luz: cor, portal: '', login: '', passos: [] };
}

// Mesma lista e ordem de Agreements.Fields no servidor.
const CAMPOS_CONVENIO = [
  ['cidade', 'Cidade ou âmbito'], ['tipo', 'Tipo de órgão'], ['orgao', 'Órgão'], ['publico', 'Público-alvo'], ['nao_aceitos', 'Não aceitos'],
  ['prazo', 'Prazo máximo'], ['prazo_meses', 'Prazo (meses, só número)'], ['margem', 'Margem'], ['margem_pct', 'Margem (%, só número)'], ['margem_seguranca', 'Margem de segurança'],
  ['idade_valor', 'Idade x valor'], ['valor_max_mil', 'Valor máximo (R$ mil, só número)'], ['qtd_contratos', 'Quantidade de contratos'], ['venda_digital', 'Venda digital'],
  ['averbadora', 'Averbadora'], ['codigo_online', 'Código online'], ['abater_neg', 'Abater margem negativa'], ['agregar_refin', 'Agregar margem no refin'],
  ['unificar_refin', 'Unificar parcelas no refin'], ['portab', 'Portabilidade (agregar / unificar)'], ['docs', 'Documentos'], ['atencao', 'Atenção'],
  ['simplificado', 'Roteiro Simplificado no portal (Sim/Não)'], ['lido', 'Roteiro lido (Sim/Não)'], ['ultima_data', 'Data do roteiro'], ['arquivos', 'Arquivo no portal do banco'], ['pagina', 'Página aproximada na lista do portal'],
];
const ROTULO_CONVENIO = Object.fromEntries(CAMPOS_CONVENIO);

const cacheDetalhes = new WeakMap();
function detalhes(i) {
  if (i.details && typeof i.details === 'object') return i.details;
  if (!i.detailsJson) return {};
  let d = cacheDetalhes.get(i);
  if (!d) { try { d = JSON.parse(i.detailsJson); } catch { d = null; } if (!d || typeof d !== 'object') d = {}; cacheDetalhes.set(i, d); }
  return d;
}
const temConvenio = (i) => Object.keys(detalhes(i)).length > 0;
const naoLido = (i) => temConvenio(i) && semAcento(detalhes(i).lido || '') === 'nao';
const rotuloRank = (n) => (n === 1 ? '1º em vendas' : `${n}º em vendas`);
const sim = (v) => /^sim\b/i.test(texto(v).trim());

// ------------------------------------------------------------------ vista
function filtrosRoteiros() {
  if (!E.rot) E.rot = { q: '', banco: '', uf: '', tipo: '', dados: '', ordem: 'ranking', modo: 'cartoes' };
  return E.rot;
}

function desenharRoteiros() {
  const f = filtrosRoteiros(), alvo = $('v-roteiros');
  if (f.modo === 'lista') { desenharLista('roteiros'); alvo.prepend(barraModo()); return; }
  const todos = itensDe('script');
  alvo.replaceChildren(barraModo(), numerosRoteiros(todos), podioVendas(todos), cartaoRoteiros(todos), porUf(todos));
}

function barraModo() {
  const f = filtrosRoteiros();
  const seg = el('div', { className: 'segmento', role: 'group', 'aria-label': 'Modo de exibição' });
  for (const [v, r] of [['cartoes', 'Cartões'], ['lista', 'Lista']]) {
    const b = el('button', { type: 'button', textContent: r, 'aria-pressed': String(f.modo === v) });
    b.onclick = () => { f.modo = v; desenharRoteiros(); };
    seg.appendChild(b);
  }
  return el('div', { className: 'rot-modo' }, seg);
}

const tile = (valor, rotulo, nota, destaque) => el('div', { className: `numero${destaque ? ' destaque' : ''}` }, el('b', { textContent: mil(valor) }), el('span', { textContent: rotulo }), nota ? el('small', { textContent: nota }) : null);
function numerosRoteiros(todos) {
  const bancos = new Set(todos.map((i) => i.bank).filter(Boolean)), ufs = new Set(todos.map((i) => i.uf).filter(Boolean));
  const conv = todos.filter(temConvenio), lidos = conv.filter((i) => !naoLido(i)), rank = todos.filter((i) => i.salesRank > 0);
  return el('div', { className: 'numeros' },
    tile(todos.length, 'Roteiros', `${mil(todos.filter((i) => i.enabled).length)} em uso pela IA`),
    tile(bancos.size, 'Bancos', [...bancos].sort().slice(0, 3).join(', ') + (bancos.size > 3 ? '…' : '')),
    tile(ufs.size, 'UFs', [...ufs].sort().join(', ')),
    tile(lidos.length, 'Convênios com regras lidas', conv.length - lidos.length ? `${mil(conv.length - lidos.length)} só com o arquivo no portal` : 'Todos os convênios têm regras'),
    tile(rank.length, 'No ranking de venda', rank.length ? 'O que a IA indica como "melhor roteiro"' : 'Marque a posição de venda nos cartões', !rank.length));
}

function podioVendas(todos) {
  const f = filtrosRoteiros();
  if (f.podioAberto === undefined) f.podioAberto = true;
  const bancos = [...new Set(todos.map((i) => i.bank).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'pt-BR'));
  const linhas = bancos.filter((banco) => !f.podioBanco || banco === f.podioBanco).map((banco) => {
    const b = bancoDe(banco), ranked = todos.filter((i) => i.bank === banco && i.salesRank > 0).sort((x, y) => x.salesRank - y.salesRank);
    const chip = el('span', { className: 'rot-chip', style: `--bc:${b.cor}`, textContent: banco });
    const pos = el('div', { className: 'rot-posicoes' });
    if (!ranked.length) pos.appendChild(el('span', { className: 'nota', textContent: 'Sem ranking. Abra um roteiro deste banco e marque a posição de venda; até lá a IA diz que a Pontual ainda não definiu o melhor.' }));
    for (const i of ranked.slice(0, 5)) {
      const d = detalhes(i), bt = el('button', { type: 'button', className: `rot-pos r${Math.min(i.salesRank, 4)}`, title: i.salesNote ? `${rotuloRank(i.salesRank)} · ${i.salesNote}` : rotuloRank(i.salesRank) },
        el('i', { textContent: `${i.salesRank}º` }), el('span', { textContent: d.orgao || i.title }));
      bt.onclick = () => gavetaItem(i.id);
      pos.appendChild(bt);
    }
    if (ranked.length > 5) pos.appendChild(el('span', { className: 'nota', textContent: `+${mil(ranked.length - 5)}` }));
    return el('div', { className: 'rot-banco' }, chip, pos);
  });
  // Lista suspensa de banco e botão de seta: o pódio abre e recolhe (pedido do dono, 25/11/2026).
  const escolhaBanco = el('select', { className: 'campo', 'aria-label': 'Banco do pódio' }, el('option', { value: '', textContent: 'Todos os bancos' }), ...bancos.map((b) => el('option', { value: b, textContent: b, selected: f.podioBanco === b })));
  escolhaBanco.onclick = (e) => e.stopPropagation();
  escolhaBanco.onchange = () => { f.podioBanco = escolhaBanco.value; f.podioAberto = true; desenharRoteiros(); };
  const seta = el('button', { type: 'button', className: 'icone rot-seta', 'aria-expanded': String(f.podioAberto), 'aria-controls': 'rot-podio', title: f.podioAberto ? 'Recolher' : 'Abrir', 'aria-label': f.podioAberto ? 'Recolher o pódio' : 'Abrir o pódio' }, ic(f.podioAberto ? 'recolher' : 'expandir', 18));
  seta.onclick = (e) => { e.stopPropagation(); f.podioAberto = !f.podioAberto; desenharRoteiros(); };
  const topo = el('div', { className: 'cartao-topo rot-topo-podio' }, el('div', {}, el('h2', { textContent: 'O que mais vende em cada banco' }), el('p', { textContent: 'Quando a atendente pergunta "qual o melhor roteiro do banco", a IA responde com o 1º daqui e cita os seguintes. A posição é marcada pela gestão, no cartão de cada roteiro.' })), escolhaBanco, seta);
  topo.onclick = () => { f.podioAberto = !f.podioAberto; desenharRoteiros(); };
  const corpo = linhas.length ? el('div', { className: 'rot-podio', id: 'rot-podio' }, ...linhas) : vazio('Nenhum roteiro com banco', 'Preencha o campo "Banco ou assunto" dos roteiros para eles aparecerem aqui.');
  corpo.hidden = !f.podioAberto;
  return el('div', { className: 'cartao' }, topo, corpo);
}

function cartaoRoteiros(todos) {
  const f = filtrosRoteiros();
  const unicos = (fn) => [...new Set(todos.map(fn).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'pt-BR'));
  const escolha = (chave, rotulo, opcoes) => {
    const s = el('select', { className: 'campo', 'aria-label': rotulo }, el('option', { value: '', textContent: rotulo }), ...opcoes.map(([v, r]) => el('option', { value: v, textContent: r, selected: f[chave] === v })));
    s.onchange = () => { f[chave] = s.value; pintarCartoes(todos); };
    return s;
  };
  const busca = el('input', { className: 'campo cresce', type: 'search', placeholder: 'Filtrar por órgão, cidade, averbadora, banco…', 'aria-label': 'Filtrar os roteiros', value: f.q });
  busca.oninput = () => { f.q = busca.value; pintarCartoes(todos); };
  const novo = botao('Novo roteiro', 'mais', 'btn'); novo.onclick = () => abrirFormulario(null, 'script');
  const grade = el('div', { className: 'rot-grade', id: 'rot-grade' });
  const cartao = el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Roteiros por convênio' }), el('p', { textContent: 'Role a caixa para ver todos. Clique no cartão para ver tudo, marcar o ranking de venda ou ver onde baixar o arquivo no portal.' })), novo),
    el('div', { className: 'ferramentas' }, busca,
      escolha('banco', 'Todos os bancos', unicos((i) => i.bank).map((b) => [b, b])),
      escolha('uf', 'Todas as UFs', unicos((i) => i.uf).map((u) => [u, u])),
      escolha('tipo', 'Todos os tipos de órgão', unicos((i) => detalhes(i).tipo).map((t) => [t, t])),
      escolha('dados', 'Todos os roteiros', [['lido', 'Convênios com regras lidas'], ['arquivo', 'Só o arquivo no portal (não lido)'], ['texto', 'Roteiros em texto (sem convênio)'], ['ranking', 'No ranking de venda'], ['fora', 'Fora de uso']]),
      escolha('ordem', 'Ordem', [['ranking', 'Ordem: ranking de venda'], ['nome', 'Ordem: nome'], ['atualizado', 'Ordem: mais recentes'], ['prazo', 'Ordem: maior prazo'], ['margem', 'Ordem: maior margem'], ['valor', 'Ordem: maior valor']])),
    grade);
  queueMicrotask(() => pintarCartoes(todos));
  return cartao;
}

function pintarCartoes(todos) {
  const f = filtrosRoteiros(), q = semAcento(f.q), grade = $('rot-grade');
  if (!grade) return;
  const num = (i, k) => Number(detalhes(i)[k]) || 0;
  let lista = todos.filter((i) => {
    const d = detalhes(i);
    if (f.banco && i.bank !== f.banco) return false;
    if (f.uf && i.uf !== f.uf) return false;
    if (f.tipo && d.tipo !== f.tipo) return false;
    if (f.dados === 'lido' && !(temConvenio(i) && !naoLido(i))) return false;
    if (f.dados === 'arquivo' && !naoLido(i)) return false;
    if (f.dados === 'texto' && temConvenio(i)) return false;
    if (f.dados === 'ranking' && !(i.salesRank > 0)) return false;
    if (f.dados === 'fora' && i.enabled) return false;
    return !q || semAcento(`${i.title} ${i.description} ${i.bank} ${i.uf} ${i.keywords} ${Object.values(d).join(' ')}`).includes(q);
  });
  const porNome = (a, b) => a.title.localeCompare(b.title, 'pt-BR');
  const ordens = {
    ranking: (a, b) => (a.salesRank > 0 ? a.salesRank : 999) - (b.salesRank > 0 ? b.salesRank : 999) || a.bank.localeCompare(b.bank, 'pt-BR') || porNome(a, b),
    nome: porNome, atualizado: (a, b) => new Date(b.updatedAt) - new Date(a.updatedAt),
    prazo: (a, b) => num(b, 'prazo_meses') - num(a, 'prazo_meses') || porNome(a, b),
    margem: (a, b) => num(b, 'margem_pct') - num(a, 'margem_pct') || porNome(a, b),
    valor: (a, b) => num(b, 'valor_max_mil') - num(a, 'valor_max_mil') || porNome(a, b),
  };
  lista = lista.slice().sort(ordens[f.ordem] || ordens.ranking);
  if (!lista.length) { grade.replaceChildren(vazio(todos.length ? 'Nada com esses filtros' : 'Nenhum roteiro cadastrado', todos.length ? 'Limpe a busca ou troque os filtros.' : 'Use "Novo roteiro" para cadastrar o primeiro.')); return; }
  grade.replaceChildren(...lista.map(cartaoRoteiro));
}

function cartaoRoteiro(i) {
  const d = detalhes(i), b = bancoDe(i.bank), conv = temConvenio(i), semLeitura = naoLido(i);
  const c = el('article', { className: `rot-cartao${i.enabled ? '' : ' fora'}${i.salesRank === 1 ? ' top' : ''}`, style: `--bc:${b.cor};--bl:${b.luz}`, tabIndex: 0, 'aria-label': `Abrir ${i.title}` });
  c.onclick = () => gavetaItem(i.id);
  c.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); gavetaItem(i.id); } };

  const topo = el('div', { className: 'rot-topo' });
  if (i.bank) topo.appendChild(el('span', { className: 'rot-chip', style: `--bc:${b.cor}`, textContent: i.bank }));
  if (i.uf) topo.appendChild(el('span', { className: 'rot-uf', textContent: i.uf }));
  if (!i.enabled) topo.appendChild(selo('disabled liso', 'Fora de uso'));
  if (i.salesRank > 0) topo.appendChild(el('span', { className: `rot-rank r${Math.min(i.salesRank, 4)}`, textContent: rotuloRank(i.salesRank), title: i.salesNote || '' }));
  c.appendChild(topo);
  c.appendChild(el('h3', { textContent: d.orgao || i.title }));
  const sub = [d.tipo, d.cidade && d.cidade !== d.tipo ? d.cidade : null, d.ultima_data ? `roteiro de ${d.ultima_data}` : null].filter(Boolean).join(' · ');
  c.appendChild(el('p', { className: 'rot-sub', textContent: sub || (conv ? '' : `${i.category || 'Roteiro'} · atualizado ${quando(i.updatedAt)}`) }));

  if (conv && !semLeitura) {
    const fato = (r, v, txt) => el('div', { className: 'rot-fato' }, el('small', { textContent: r }), el('b', { className: txt ? 'txt' : '', textContent: v || '—', title: v || '' }));
    c.appendChild(el('div', { className: 'rot-fatos' },
      fato('Prazo', d.prazo_meses ? `${d.prazo_meses} m` : d.prazo),
      fato('Margem', d.margem_pct ? `${d.margem_pct}%` : d.margem),
      fato('Valor máx.', d.valor_max_mil ? `R$ ${mil(d.valor_max_mil)} mil` : ''),
      fato('Averbadora', d.averbadora, true)));
    const marcas = [];
    if (sim(d.venda_digital)) marcas.push('Venda digital'); else if (d.venda_digital) marcas.push('Venda digital: ver regra');
    if (sim(d.codigo_online)) marcas.push('Código online');
    if (sim(d.agregar_refin)) marcas.push('Refin agrega margem');
    if (sim(d.unificar_refin)) marcas.push('Refin unifica');
    if (sim(d.abater_neg)) marcas.push('Abate margem negativa');
    if (marcas.length) c.appendChild(el('div', { className: 'marcas' }, ...marcas.map((m) => el('span', { textContent: m }))));
  } else if (semLeitura) {
    c.appendChild(el('p', { className: 'rot-aviso', textContent: `Roteiro completo ainda não lido. No portal do ${i.bank || 'banco'}: ${d.arquivos || 'arquivo não informado'}` }));
  } else {
    if (i.description) c.appendChild(el('p', { className: 'rot-resumo', textContent: i.description }));
  }
  if (d.atencao) c.appendChild(el('p', { className: 'rot-atencao', textContent: d.atencao }));

  const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(i.enabled), 'aria-label': `${i.enabled ? 'Desativar' : 'Ativar'} ${i.title}`, disabled: !i.chars });
  chave.onclick = (e) => { e.stopPropagation(); agir(chave, () => api('PUT', `/neural/items/${i.id}`, { enabled: !i.enabled }), `${i.title}: ${i.enabled ? 'desativado' : 'ativado'}.`); };
  const rank = botao(i.salesRank > 0 ? 'Ranking' : 'Marcar venda', 'raio', 'btn claro mini'); rank.onclick = (e) => { e.stopPropagation(); abrirRanking(i); };
  const editar = iconeBotao('editar', `Editar ${i.title}`); editar.onclick = async (e) => { e.stopPropagation(); try { abrirFormulario(await api('GET', `/neural/items/${i.id}`)); } catch (erro) { recado(erro.message, true); } };
  c.appendChild(el('div', { className: 'rot-rodape' }, chave, el('span', { className: 'cresce' }), rank, editar));
  return c;
}

function porUf(todos) {
  const g = {};
  for (const i of todos) { if (!i.uf) continue; const o = (g[i.uf] ??= { total: 0, lidos: 0 }); o.total++; if (temConvenio(i) && !naoLido(i)) o.lidos++; }
  const ufs = Object.keys(g).sort((a, b) => g[b].total - g[a].total || a.localeCompare(b));
  if (!ufs.length) return el('span');
  const mx = Math.max(...ufs.map((u) => g[u].total));
  const barras = el('div', { className: 'barras' }, ...ufs.map((u) => el('div', {}, el('span', { textContent: `${u} · ${mil(g[u].lidos)} com regras lidas` }), el('b', { textContent: mil(g[u].total) }), el('i', { style: `--p:${(g[u].total / mx) * 100}%` }))));
  return el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {}, el('h2', { textContent: 'Convênios por UF' }), el('p', { textContent: 'Quantos roteiros há em cada estado e quantos já têm as regras lidas.' }))),
    el('div', { className: 'cartao-corpo' }, barras));
}

// ------------------------------------------------------- gaveta e formulário
// Seções extras da gaveta de um roteiro (chamado por gavetaItem em neural.js).
function secoesConvenio(i) {
  const d = detalhes(i), b = bancoDe(i.bank), partes = [];
  if (Object.keys(d).length) {
    const dl = el('dl', { className: 'dados' });
    for (const [k] of CAMPOS_CONVENIO) if (d[k] && !['arquivos', 'pagina'].includes(k)) dl.appendChild(dado(ROTULO_CONVENIO[k], d[k]));
    for (const k of Object.keys(d).sort()) if (!ROTULO_CONVENIO[k]) dl.appendChild(dado(k.replace(/_/g, ' '), d[k]));
    partes.push(secao('Dados do convênio', dl));
  }
  if (d.arquivos || b.portal) {
    const filhos = [];
    if (d.arquivos) filhos.push(el('p', { className: 'nota', textContent: 'Procure por este nome no portal do banco:' }), el('div', { className: 'rot-arquivo', textContent: d.arquivos }));
    if (d.pagina) filhos.push(el('p', { className: 'nota', textContent: `Página aproximada da lista: ${d.pagina} (muda quando entram arquivos novos).` }));
    if (b.passos.length) filhos.push(el('ol', { className: 'rot-passos' }, ...b.passos.map((p) => el('li', { textContent: p }))));
    const links = [];
    if (b.portal) links.push(el('a', { className: 'btn mini', href: b.portal, target: '_blank', rel: 'noopener noreferrer', textContent: `Roteiros do ${i.bank}` }));
    if (b.login) links.push(el('a', { className: 'btn claro mini', href: b.login, target: '_blank', rel: 'noopener noreferrer', textContent: 'Login do correspondente' }));
    if (links.length) filhos.push(el('div', { className: 'botoes' }, ...links));
    filhos.push(el('p', { className: 'nota', textContent: 'O download exige o login de correspondente. Não conseguiu baixar? Procure o Gerente Comercial.' }));
    partes.push(secao('Onde baixar o roteiro', ...filhos));
  }
  return partes;
}

function textoRanking(i) {
  if (!(i.salesRank > 0)) return 'Sem ranking';
  return `${rotuloRank(i.salesRank)} no ${i.bank || 'banco'}${i.salesNote ? ` · ${i.salesNote}` : ''}`;
}

let rankAberto = null;
function abrirRanking(i) {
  rankAberto = i;
  const banco = i.bank || '';
  const outros = itensDe('script').filter((x) => x.id !== i.id && x.bank === banco && x.salesRank > 0).sort((a, b) => a.salesRank - b.salesRank);
  $('rank-titulo').textContent = `Ranking de venda · ${i.title}`;
  $('rank-explica').textContent = banco
    ? (outros.length ? `Hoje no ${banco}: ${outros.map((x) => `${x.salesRank}º ${detalhes(x).orgao || x.title}`).join('; ')}. Se escolher uma posição ocupada, os dois trocam de lugar.` : `Nenhum roteiro do ${banco} tem ranking ainda. O 1º é o que a IA indica como "melhor roteiro".`)
    : 'Este roteiro está sem banco. Preencha o campo "Banco ou assunto" antes: o ranking é por banco.';
  const sel = $('rank-pos'); sel.replaceChildren(el('option', { value: '0', textContent: 'Sem ranking' }), ...Array.from({ length: 10 }, (_, k) => el('option', { value: String(k + 1), textContent: k === 0 ? '1º: o que mais vende' : `${k + 1}º` })));
  sel.value = String(i.salesRank > 0 ? Math.min(i.salesRank, 10) : 0);
  $('rank-nota').value = i.salesNote || '';
  $('rank-erro').hidden = true;
  $('dlg-ranking').showModal();
  sel.focus();
}
aoEnviar('dlg-ranking', 'rank-erro', async () => {
  const i = rankAberto, pos = Number($('rank-pos').value), nota = $('rank-nota').value.trim();
  exigir(i, 'Nenhum roteiro selecionado.');
  exigir(pos === 0 || i.bank, 'Preencha o banco do roteiro antes de marcar o ranking.');
  const ocupante = pos > 0 ? itensDe('script').find((x) => x.id !== i.id && x.bank === i.bank && x.salesRank === pos) : null;
  await api('PUT', `/neural/items/${i.id}`, { salesRank: pos, salesNote: nota });
  if (ocupante) await api('PUT', `/neural/items/${ocupante.id}`, { salesRank: i.salesRank > 0 ? i.salesRank : 0 });
  await carregar();
  if (E.gaveta === i.id) gavetaItem(i.id);
  return pos > 0 ? `${i.title} agora é ${rotuloRank(pos)} no ${i.bank}.${ocupante ? ` ${ocupante.title} ${i.salesRank > 0 ? `passou para ${rotuloRank(i.salesRank)}` : 'saiu do ranking'}.` : ''}` : `${i.title} saiu do ranking de venda.`;
});

// Campos do convênio no formulário de roteiro (chamados por abrirFormulario e pelo envio em neural.js).
function pintarConvenio(d) {
  const caixa = $('item-convenio'); d = d && typeof d === 'object' ? d : {};
  caixa.replaceChildren(...CAMPOS_CONVENIO.map(([k, r]) => el('label', { className: ['publico', 'nao_aceitos', 'idade_valor', 'atencao', 'arquivos', 'docs', 'prazo', 'qtd_contratos'].includes(k) ? 'toda' : '' }, r,
    el('input', { className: 'campo', 'data-campo': k, maxLength: 1000, autocomplete: 'off', value: d[k] || '' }))));
  for (const k of Object.keys(d).sort()) if (!ROTULO_CONVENIO[k]) caixa.appendChild(el('label', {}, k.replace(/_/g, ' '), el('input', { className: 'campo', 'data-campo': k, maxLength: 1000, autocomplete: 'off', value: d[k] })));
  $('item-convenio-caixa').open = Object.keys(d).length > 0;
}
function lerConvenio() {
  const d = {};
  for (const c of $('item-convenio').querySelectorAll('input[data-campo]')) d[c.dataset.campo] = c.value.trim();
  return d;
}
