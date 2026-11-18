// Central do TI — seção "Assistente": chave da IA, roteiros operacionais, perfil de cada atendente e conversas.
// Carregado depois de app.js e usa as utilidades dele ($, el, ic, api, pode, agir, aoEnviar, recado, S...).
// As mesmas regras valem aqui: nada que vem do servidor entra na página como HTML.
'use strict';

const Assistente = (() => {
  let dados = null, falha = null;
  let roteiroAberto = null;   // nulo = roteiro novo
  let aoSalvarTexto = null;   // o que o diálogo de texto grava (orientações gerais ou de uma atendente)

  const mil = (n) => Number(n || 0).toLocaleString('pt-BR');
  const caminhoRoteiro = (id) => `/assistant/scripts/${encodeURIComponent(id)}`;
  const caminhoPerfil = (id) => `/assistant/profiles/${encodeURIComponent(id)}`;

  async function carregar() {
    try { dados = await api('GET', '/assistant'); falha = null; } catch (e) { if (e.status === 401) return; falha = e.message; }
    S.assin.assistente = null;
    desenhar();
  }
  // Ação direta (interruptor, excluir): usa o agir() da Central e recarrega a seção.
  async function fazer(botao, acao, ok) { if (await agir(botao, acao, { ok })) await carregar(); }

  function abrir() { dados = null; falha = null; carregar(); }

  // ----------------------------------------------------------------- desenho
  function desenhar() {
    if (S.secao !== 'assistente' || !mudou('assistente', [dados, falha])) return;
    if (!dados) {
      const aviso = el('p', { className: 'vazio', textContent: falha || 'Carregando…' });
      trocar($('ia-conexao'), [aviso]); trocar($('tb-roteiros'), []); trocar($('tb-perfis'), []);
      return;
    }
    desenharConexao(); desenharRoteiros(); desenharPerfis();
  }

  function desenharConexao() {
    const c = dados.settings, edita = pode('config.edit'), chaves = pode('keys.manage');
    const situacao = c.hasKey
      ? el('div', { className: 'ia-linha' }, selo('online', 'Chave cadastrada'),
        el('span', { textContent: c.keyFromServerConfig ? 'A chave vem da configuração do servidor.' : 'A assistente aparece no Terminal das atendentes ligadas abaixo.' }))
      : el('div', { className: 'ia-linha' }, selo('alerta', 'Sem chave'),
        el('span', { textContent: 'A assistente só aparece para as atendentes depois que a chave da IA for cadastrada.' }));

    const botoes = el('div', { className: 'acoes junto' });
    if (chaves) {
      const cadastrar = el('button', { type: 'button', className: 'btn', textContent: c.hasKey ? 'Trocar a chave' : 'Cadastrar a chave', 'data-k': 'ia-chave' });
      cadastrar.onclick = () => { $('ia-chave-valor').value = ''; $('ia-chave-erro').hidden = true; $('dlg-ia-chave').showModal(); $('ia-chave-valor').focus(); };
      botoes.append(cadastrar);
    }
    if (edita && c.hasKey) {
      const testar = el('button', { type: 'button', className: 'btn claro', textContent: 'Testar a conexão', 'data-k': 'ia-testar' });
      testar.onclick = () => agir(testar, () => api('POST', '/assistant/test'), { ok: 'A IA respondeu: a chave e o modelo estão funcionando.' });
      botoes.append(testar);
    }
    if (chaves && c.hasKey && !c.keyFromServerConfig) {
      const remover = el('button', { type: 'button', className: 'btn claro', textContent: 'Remover a chave', 'data-k': 'ia-remover' });
      remover.onclick = async () => {
        const sim = await confirmar({ titulo: 'Remover a chave da IA?', texto: 'O botão da assistente some do Terminal de todas as atendentes até uma nova chave ser cadastrada. As conversas e os roteiros continuam guardados.', botao: 'Remover' });
        if (sim) fazer(remover, () => api('PUT', '/assistant/settings', { removeKey: true }), 'A chave da IA foi removida.');
      };
      botoes.append(remover);
    }

    const modelo = el('select', { className: 'campo', id: 'ia-modelo', disabled: !edita, 'data-k': 'ia-modelo' },
      ...(c.models || []).map((m) => el('option', { value: m.id, textContent: m.label, selected: m.id === c.model })));
    modelo.onchange = () => fazer(modelo, () => api('PUT', '/assistant/settings', { model: modelo.value }), 'O modelo da assistente foi trocado.');

    const orientacoes = el('button', { type: 'button', className: 'btn claro', textContent: c.instructions ? 'Editar as orientações gerais' : 'Escrever orientações gerais', disabled: !edita, 'data-k': 'ia-gerais' });
    orientacoes.onclick = () => abrirTexto({
      titulo: 'Orientações gerais',
      explicacao: 'Valem para todas as atendentes. Ex.: como a Pontual quer que a assistente trate dúvidas fora do roteiro, a quem encaminhar, o tom das respostas.',
      valor: c.instructions, limite: dados.limits.generalInstructions,
      salvar: (escrito) => api('PUT', '/assistant/settings', { instructions: escrito }), ok: 'As orientações gerais foram salvas.',
    });

    trocar($('ia-conexao'), [situacao, botoes,
      el('div', { className: 'ia-grade' },
        el('label', { className: 'ia-campo' }, 'Modelo de IA', modelo, el('span', { className: 'dica', textContent: 'Modelos mais capazes respondem melhor a roteiros longos e custam mais por pergunta.' })),
        el('div', { className: 'ia-campo' }, el('span', { textContent: 'Orientações gerais' }), orientacoes,
          el('span', { className: 'dica', textContent: c.instructions ? `${mil(c.instructions.length)} caracteres escritos.` : 'Nenhuma orientação geral escrita.' })))]);
  }

  function desenharRoteiros() {
    const edita = pode('config.edit'), lista = dados.scripts || [];
    $('novo-roteiro').hidden = !edita;
    const ligados = lista.filter((r) => r.enabled).reduce((soma, r) => soma + r.chars, 0);
    $('roteiros-frase').textContent = lista.length
      ? `${plural(lista.length, 'roteiro', 'roteiros')}; ${mil(ligados)} de ${mil(dados.limits.allScripts)} caracteres em uso. A assistente lê todos os roteiros ligados a cada pergunta.`
      : 'Cole aqui o roteiro de cada banco. A assistente só conhece o que estiver cadastrado e ligado.';
    const colunas = ['Roteiro', 'Tamanho', 'Atualizado', 'Em uso', ''];
    const cab = el('thead', {}, el('tr', {}, ...colunas.map((t) => (t ? el('th', { textContent: t, scope: 'col' }) : el('th', { scope: 'col', className: 'estreita' }, el('span', { className: 'so-leitor', textContent: 'Ações' }))))));
    if (!lista.length) {
      trocar($('tb-roteiros'), [cab, el('tbody', {}, el('tr', {}, el('td', { colSpan: colunas.length, className: 'vazio', textContent: 'Nenhum roteiro cadastrado. Use "Adicionar roteiro" para colar o primeiro.' })))]);
      return;
    }
    const corpo = el('tbody', {}, ...lista.map((r) => {
      const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(r.enabled), 'aria-label': `${r.enabled ? 'Desligar' : 'Ligar'} o roteiro ${texto(r.title)}`, disabled: !edita, 'data-k': `roteiro-uso:${r.id}` });
      chave.onclick = () => fazer(chave, () => api('PUT', caminhoRoteiro(r.id), { enabled: !r.enabled }), `O roteiro ${texto(r.title)} foi ${r.enabled ? 'desligado' : 'ligado'}.`);
      const acoes = el('div', { className: 'acoes-linha' });
      const ver = el('button', { type: 'button', className: 'redondo', 'aria-label': `${edita ? 'Editar' : 'Ver'} o roteiro ${texto(r.title)}`, title: edita ? 'Editar' : 'Ver', 'data-k': `roteiro-editar:${r.id}` }, ic(edita ? 'edit' : 'eye', 18));
      ver.onclick = () => abrirRoteiro(r, ver);
      acoes.append(ver);
      if (edita) {
        const excluir = el('button', { type: 'button', className: 'redondo rubi', 'aria-label': `Excluir o roteiro ${texto(r.title)}`, title: 'Excluir', 'data-k': `roteiro-excluir:${r.id}` }, ic('trash', 18));
        excluir.onclick = async () => {
          const sim = await confirmar({ titulo: `Excluir o roteiro ${texto(r.title)}?`, texto: 'A assistente deixa de conhecer este roteiro. Se for só por um tempo, prefira desligar o interruptor "Em uso".', botao: 'Excluir' });
          if (sim) fazer(excluir, () => api('DELETE', caminhoRoteiro(r.id)), `O roteiro ${texto(r.title)} foi excluído.`);
        };
        acoes.append(excluir);
      }
      return el('tr', {},
        el('td', {}, el('div', { className: 'celula' }, avatar(r.title, 'q'), el('div', { style: 'min-width:0' }, el('b', { textContent: texto(r.title) })))),
        el('td', { className: 'num', textContent: `${mil(r.chars)} caracteres` }),
        el('td', {}, el('span', { textContent: quando(r.updatedAt) }), r.updatedBy ? el('small', { className: 'ia-por', textContent: `por ${texto(r.updatedBy)}` }) : null),
        el('td', {}, chave), el('td', { className: 'estreita' }, acoes));
    }));
    trocar($('tb-roteiros'), [cab, corpo]);
  }

  function desenharPerfis() {
    const edita = pode('config.edit'), le = pode('usage.read.individual'), lista = dados.profiles || [];
    const colunas = ['Atendente', 'Assistente', 'Orientações para ela', 'Perguntas', 'Última pergunta', ''];
    const cab = el('thead', {}, el('tr', {}, ...colunas.map((t) => (t ? el('th', { textContent: t, scope: 'col' }) : el('th', { scope: 'col', className: 'estreita' }, el('span', { className: 'so-leitor', textContent: 'Ações' }))))));
    if (!lista.length) {
      trocar($('tb-perfis'), [cab, el('tbody', {}, el('tr', {}, el('td', { colSpan: colunas.length, className: 'vazio', textContent: 'Nenhuma máquina aprovada ainda.' })))]);
      return;
    }
    const corpo = el('tbody', {}, ...lista.map((p) => {
      const nome = texto(p.attendantName).trim() || texto(p.hostname);
      const chave = el('button', { type: 'button', className: 'interruptor', role: 'switch', 'aria-checked': String(p.enabled), 'aria-label': `${p.enabled ? 'Desligar' : 'Ligar'} a assistente de ${nome}`, disabled: !edita, 'data-k': `perfil-uso:${p.deviceId}` });
      chave.onclick = () => fazer(chave, () => api('PUT', caminhoPerfil(p.deviceId), { enabled: !p.enabled }), `A assistente de ${nome} foi ${p.enabled ? 'desligada' : 'ligada'}.`);
      const acoes = el('div', { className: 'acoes-linha' });
      if (edita) {
        const editar = el('button', { type: 'button', className: 'redondo', 'aria-label': `Editar as orientações da assistente de ${nome}`, title: 'Editar as orientações', 'data-k': `perfil-editar:${p.deviceId}` }, ic('edit', 18));
        editar.onclick = () => abrirTexto({
          titulo: `Assistente de ${nome}`,
          explicacao: 'Orientações que valem só para esta atendente. Ex.: os bancos em que ela atua, se é nova na função e precisa de mais detalhe, pontos em que costuma errar.',
          valor: p.instructions, limite: dados.limits.profileInstructions,
          salvar: (escrito) => api('PUT', caminhoPerfil(p.deviceId), { instructions: escrito }), ok: `As orientações da assistente de ${nome} foram salvas.`,
        });
        acoes.append(editar);
      }
      if (le) {
        const conversas = el('button', { type: 'button', className: 'redondo', 'aria-label': `Ver as conversas de ${nome} com a assistente`, title: 'Ver as conversas', 'data-k': `perfil-conversas:${p.deviceId}` }, ic('eye', 18));
        conversas.onclick = () => abrirConversas(p.deviceId, nome);
        acoes.append(conversas);
      }
      return el('tr', {},
        el('td', {}, el('div', { className: 'celula' }, avatar(nome), el('div', {}, el('b', { textContent: nome }), el('small', { textContent: texto(p.hostname) })))),
        el('td', {}, chave),
        el('td', {}, p.instructions ? el('span', { className: 'ia-resumo', textContent: p.instructions, title: p.instructions }) : el('span', { className: 'ia-nada', textContent: 'Só as gerais' })),
        el('td', { className: 'num', textContent: mil(p.questions) }),
        el('td', { textContent: p.lastAt ? quando(p.lastAt) : 'Nunca' }),
        el('td', { className: 'estreita' }, acoes));
    }));
    trocar($('tb-perfis'), [cab, corpo]);
  }

  // ---------------------------------------------------------------- diálogos
  function contar(campo, alvo, limite) {
    const n = campo.value.length;
    alvo.textContent = `${mil(n)} de ${mil(limite)} caracteres`;
    alvo.classList.toggle('erro', n > limite);
  }

  async function abrirRoteiro(resumo, botao) {
    roteiroAberto = resumo;
    const edita = pode('config.edit');
    $('t-ia-roteiro').textContent = resumo ? (edita ? 'Editar roteiro' : 'Roteiro') : 'Adicionar roteiro';
    $('ia-roteiro-erro').hidden = true;
    $('ia-roteiro-titulo').value = resumo ? texto(resumo.title) : '';
    $('ia-roteiro-texto').value = '';
    $('ia-roteiro-titulo').disabled = !edita; $('ia-roteiro-texto').readOnly = !edita;
    $('ia-roteiro-salvar').hidden = !edita;
    if (resumo) {
      if (botao) botao.disabled = true;
      try {
        const completo = await api('GET', caminhoRoteiro(resumo.id));
        $('ia-roteiro-texto').value = texto(completo.content);
      } catch (e) {
        if (e.status !== 401) recado(e.message, true);
        return;
      } finally { if (botao && botao.isConnected) botao.disabled = false; }
    }
    contar($('ia-roteiro-texto'), $('ia-roteiro-conta'), dados.limits.script);
    $('dlg-ia-roteiro').showModal();
    (resumo ? $('ia-roteiro-texto') : $('ia-roteiro-titulo')).focus();
  }
  $('ia-roteiro-texto').oninput = () => { if (dados) contar($('ia-roteiro-texto'), $('ia-roteiro-conta'), dados.limits.script); };
  $('novo-roteiro').onclick = () => abrirRoteiro(null, null);
  aoEnviar('dlg-ia-roteiro', 'ia-roteiro-erro', async () => {
    const titulo = $('ia-roteiro-titulo').value.trim(), conteudo = $('ia-roteiro-texto').value.trim();
    exigir(titulo, 'Informe o título do roteiro (em geral, o nome do banco).');
    exigir(conteudo, 'Cole o texto do roteiro.');
    exigir(conteudo.length <= dados.limits.script, `O roteiro passou do limite de ${mil(dados.limits.script)} caracteres. Divida em duas partes.`);
    if (roteiroAberto) await api('PUT', caminhoRoteiro(roteiroAberto.id), { title: titulo, content: conteudo });
    else await api('POST', '/assistant/scripts', { title: titulo, content: conteudo });
    await carregar();
    return `O roteiro ${titulo} foi salvo. A assistente já responde com ele.`;
  });

  function abrirTexto({ titulo, explicacao, valor, limite, salvar, ok }) {
    aoSalvarTexto = { salvar, ok, limite };
    $('t-ia-texto').textContent = titulo; $('ia-texto-explicacao').textContent = explicacao;
    $('ia-texto-valor').value = texto(valor); $('ia-texto-erro').hidden = true;
    contar($('ia-texto-valor'), $('ia-texto-conta'), limite);
    $('dlg-ia-texto').showModal();
    $('ia-texto-valor').focus();
  }
  $('ia-texto-valor').oninput = () => { if (aoSalvarTexto) contar($('ia-texto-valor'), $('ia-texto-conta'), aoSalvarTexto.limite); };
  aoEnviar('dlg-ia-texto', 'ia-texto-erro', async () => {
    const escrito = $('ia-texto-valor').value.trim();
    exigir(escrito.length <= aoSalvarTexto.limite, `O texto passou do limite de ${mil(aoSalvarTexto.limite)} caracteres.`);
    await aoSalvarTexto.salvar(escrito);
    await carregar();
    return aoSalvarTexto.ok;
  });

  aoEnviar('dlg-ia-chave', 'ia-chave-erro', async () => {
    const chave = $('ia-chave-valor').value.trim();
    exigir(chave, 'Cole a chave da IA.');
    await api('PUT', '/assistant/settings', { apiKey: chave });
    $('ia-chave-valor').value = '';
    await carregar();
    return 'A chave da IA foi cadastrada. Use "Testar a conexão" para conferir.';
  });

  async function abrirConversas(deviceId, nome) {
    const alvo = $('ia-conversa-falas');
    $('t-ia-conversa').textContent = `Conversas de ${nome}`;
    alvo.replaceChildren(el('p', { className: 'vazio', textContent: 'Carregando…' }));
    $('dlg-ia-conversa').showModal();
    let lista;
    try { lista = await api('GET', `/assistant/conversations/${encodeURIComponent(deviceId)}`); } catch (e) {
      alvo.replaceChildren(el('p', { className: 'vazio erro', textContent: e.message }));
      return;
    }
    if (!Array.isArray(lista) || !lista.length) {
      alvo.replaceChildren(el('p', { className: 'vazio', textContent: `${nome} ainda não fez nenhuma pergunta à assistente.` }));
      return;
    }
    let conversa = null;
    const nos = [];
    for (const m of lista) {
      if (m.conversationId !== conversa) { conversa = m.conversationId; nos.push(el('div', { className: 'ia-divisa', textContent: quando(m.at) })); }
      nos.push(el('div', { className: `ia-fala ${m.role === 'user' ? 'eu' : 'ia'}` },
        el('small', { textContent: `${m.role === 'user' ? nome : 'Assistente'} · ${quandoCurto(m.at)}` }), el('p', { textContent: texto(m.text) })));
    }
    alvo.replaceChildren(...nos);
    alvo.scrollTop = alvo.scrollHeight;
  }

  return { abrir, desenhar };
})();
