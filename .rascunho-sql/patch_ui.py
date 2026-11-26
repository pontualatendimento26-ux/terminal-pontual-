import io, os
os.chdir(r'f:\Projetos\Terminal-Pontual\src\server\Pontual.Server\wwwroot\central\neural')

def patch(path, pairs, enc='utf-8'):
    s = io.open(path, encoding=enc).read()
    for old, new in pairs:
        assert s.count(old) == 1, (path, old[:60])
        s = s.replace(old, new)
    io.open(path, 'w', encoding=enc, newline='').write(s)

nav_old = '      <button type="button" data-ir="nexus"><i data-ic="lupa"></i><span>Consulta no Nexus</span></button>\n'
sec_old = '    <section class="vista rolagem" data-vista="nexus" hidden><div class="conteudo" id="v-nexus"></div></section>\n'
scr_old = '<script src="/central/neural/aprovar.js"></script>\n'
patch('index.html', [
    (nav_old, nav_old + '      <button type="button" data-ir="siape"><i data-ic="raio"></i><span>Margem liberada</span><em class="alerta" id="n-siape" hidden></em></button>\n'),
    (sec_old, sec_old + '    <section class="vista rolagem" data-vista="siape" hidden><div class="conteudo" id="v-siape"></div></section>\n'),
    (scr_old, scr_old + '<script src="/central/neural/siape.js"></script>\n'),
])

v_old = "  nexus: ['Consulta no Nexus', 'A atendente manda o CPF no chat da IA e vê em qual banco o cliente passa, pelo roteiro do Nexus.'],\n"
d_old = "  else if (E.vista === 'nexus') desenharNexus();\n"
n_old = "  const n = E.crm.pending.length;\n  $('n-aprovar').textContent = mil(n); $('n-aprovar').hidden = !n;\n"
patch('neural.js', [
    (v_old, v_old + "  siape: ['Margem liberada (SIAPE)', 'Todo mês a Central lê a base de margens do SIAPE, a IA escreve para quem ganhou margem e a mensagem sai pelo CRM depois de aprovada.'],\n"),
    (d_old, d_old + "  else if (E.vista === 'siape') desenharSiape();\n"),
    (n_old, n_old + "  const margem = E.crm.pending.filter((r) => r.kind === 'margem').length;\n  $('n-siape').textContent = mil(margem); $('n-siape').hidden = !margem;\n"),
])

p_old = "  const perdidos = c.pending.filter((r) => r.kind === 'perdido').length;\n"
t_old = "    tile(perdidos, 'Clientes perdidos na fila', 'Retomadas escritas pela IA'),\n"
selos_old = ("  const tempo = r.sentAt ? `enviada ${quando(r.sentAt)}` : r.waitingSince ? `sem resposta desde ${quando(r.waitingSince)}` : `escrita ${quando(r.createdAt)}`;\n"
             "  const selos = el('div', { className: 'msg-selos' },\n"
             "    r.kind === 'perdido' ? selo('pending', 'Cliente perdido') : null,\n")
selos_new = ("  const tempo = r.sentAt ? `enviada ${quando(r.sentAt)}` : r.waitingSince ? `sem resposta desde ${quando(r.waitingSince)}` : `escrita ${quando(r.createdAt)}`;\n"
             "  const margem = r.kind === 'margem';\n"
             "  const selos = el('div', { className: 'msg-selos' },\n"
             "    r.kind === 'perdido' ? selo('pending', 'Cliente perdido') : margem ? selo('system', 'Margem liberada') : null,\n")
bal_old = "    el('div', { className: 'msg-balao cliente' }, el('b', { textContent: 'Cliente escreveu' }), el('span', { textContent: r.incoming })),\n"
bal_new = "    el('div', { className: 'msg-balao cliente' }, el('b', { textContent: margem ? 'Dados da base do SIAPE' : 'Cliente escreveu' }), el('span', { textContent: r.incoming })),\n"
eq_old = "    partes.push(el('div', { className: 'msg-balao equipe' }, el('b', { textContent: 'Resposta da IA · você pode editar' }), campo));\n"
eq_new = "    partes.push(el('div', { className: 'msg-balao equipe' }, el('b', { textContent: margem ? 'Primeira mensagem da IA · você pode editar' : 'Resposta da IA · você pode editar' }), campo));\n"
us_old = "    if (!E.crmUsuarios && r.kind !== 'perdido') carregarUsuariosCrm().then(preencherPara);\n"
us_new = "    if (!E.crmUsuarios && r.kind !== 'perdido' && !margem) carregarUsuariosCrm().then(preencherPara);\n"
ac_old = "    const acoes = r.kind === 'perdido' ? [aprovar, finalizado, el('span', { className: 'empurra' }), recusar] : [aprovar, humano, para, el('span', { className: 'empurra' }), recusar];\n"
ac_new = ("    const acoes = r.kind === 'perdido' ? [aprovar, finalizado, el('span', { className: 'empurra' }), recusar]\n"
          "      : margem ? [aprovar, el('span', { className: 'empurra' }), recusar] : [aprovar, humano, para, el('span', { className: 'empurra' }), recusar];\n")
fim_old = ("  partes.push(detalhesConversa('Ver a conversa inteira', r.transcript, `conversa:${r.id}`));\n"
           "  return el('article', { className: `msg${r.kind === 'perdido' ? ' perdido' : ''}${editavel ? '' : ' tratada'}` }, ...partes);\n")
fim_new = ("  if (!margem) partes.push(detalhesConversa('Ver a conversa inteira', r.transcript, `conversa:${r.id}`));\n"
           "  return el('article', { className: `msg${r.kind === 'perdido' ? ' perdido' : ''}${margem ? ' margem' : ''}${editavel ? '' : ' tratada'}` }, ...partes);\n")
patch('aprovar.js', [
    (p_old, p_old + "  const margens = c.pending.filter((r) => r.kind === 'margem').length;\n"),
    (t_old, t_old + "    tile(margens, 'Margem liberada na fila', 'Primeiras mensagens do SIAPE'),\n"),
    (selos_old, selos_new), (bal_old, bal_new), (eq_old, eq_new), (us_old, us_new), (ac_old, ac_new), (fim_old, fim_new),
])

css_old = '.msg.perdido::before { content: ""; position: absolute; left: 0; top: 0; bottom: 0; width: 3px; background: var(--ambar); }\n'
css_new = css_old + '''.msg.margem::before { content: ""; position: absolute; left: 0; top: 0; bottom: 0; width: 3px; background: #ffe08f; }

/* ------------------------------------------------------------ margem liberada (SIAPE) */
.siape-layout { display: grid; grid-template-columns: minmax(320px, 1fr) minmax(320px, 1fr); gap: 14px; margin-top: 14px; align-items: start; }
.siape-coluna { display: grid; gap: 14px; }
.siape-grade { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
.siape-campo { display: grid; gap: 4px; font-size: 12.5px; color: var(--suave); }
.siape-campo small { color: var(--apagado); font-size: 11.5px; }
.siape-campo input[type="number"] { font-variant-numeric: tabular-nums; }
.siape-lista { display: grid; gap: 12px; }
@media (max-width: 1100px) { .siape-layout { grid-template-columns: 1fr; } }
@media (max-width: 640px) { .siape-grade { grid-template-columns: 1fr; } }
'''
patch('neural.css', [(css_old, css_new)])
print('ok')
