import io
p='src/server/Pontual.Server/wwwroot/central/neural/neural.js'
s=io.open(p,encoding='utf-8').read()
def rep(a,b):
    global s
    assert s.count(a)==1,(a,s.count(a))
    s=s.replace(a,b)

rep('''    c.pending.length ? el('div', { className: 'cartao' }, filtro) : null,''','''    cartaoPerdidos(c),
    c.pending.length ? el('div', { className: 'cartao' }, filtro) : null,''')

rep('''function desenharAprovar() {''','''// A busca dos clientes perdidos: no funil Atendimento Portabilidade, quem escreveu por último e está sem resposta
// há mais de um dia (até 15 dias). Roda sozinha a cada meia hora, das 8h às 18h, de segunda a sábado, se ligada.
function cartaoPerdidos(c) {
  const [quandoRodou, , , nota] = (c.lostLastRun || '').split('|');
  const ligar = botao(c.lostEnabled ? 'Desligar a busca automática' : 'Ligar a busca automática', null, c.lostEnabled ? 'btn claro mini' : 'btn mini');
  ligar.onclick = () => agirCrm(ligar, () => api('PUT', '/neural/crm/lost', { enabled: !c.lostEnabled }), c.lostEnabled ? 'Busca automática desligada.' : 'Busca automática ligada: roda a cada meia hora, das 8h às 18h.');
  const agora = botao('Buscar agora', null);
  agora.title = 'Procura agora os clientes sem resposta. Cada conversa lida usa a IA (no máximo 25 por busca).';
  agora.onclick = async () => {
    agora.disabled = true; agora.textContent = 'Buscando… (pode levar alguns minutos)';
    try { const r = await api('POST', '/neural/crm/lost/scan'); recado(r.message); }
    catch (e) { recado(e.message, true); }
    finally { agora.disabled = false; agora.textContent = 'Buscar agora'; await carregarCrm(); }
  };
  return el('div', { className: 'cartao' },
    el('div', { className: 'cartao-topo' }, el('div', {},
      el('h2', { textContent: 'Clientes perdidos' }),
      el('p', { textContent: 'A IA procura no funil Atendimento Portabilidade os clientes que escreveram por último e estão sem resposta há mais de um dia (até 15 dias para trás). Ela lê a conversa, vê quem é a responsável no CRM e escreve a retomada no jeito dela. Quem só mandou "ok" ou "obrigado" fica de fora.' })),
      selo(c.lostEnabled ? 'active' : 'disabled', c.lostEnabled ? 'Busca automática ligada' : 'Busca automática desligada')),
    el('div', { className: 'botoes' }, ligar, agora),
    quandoRodou ? el('p', { className: 'nota', textContent: `Última busca ${quando(quandoRodou)}: ${nota}` }) : el('p', { className: 'nota', textContent: 'A busca ainda não rodou.' }));
}

function desenharAprovar() {''')

rep('''    el('header', {}, selo(classe, rotulo), el('b', { textContent: nome }), r.attendant ? el('small', { textContent: `Atendente: ${r.attendant}` }) : null,''',
'''    el('header', {}, selo(classe, rotulo), r.kind === 'perdido' ? selo('pending', 'Cliente perdido') : null, el('b', { textContent: nome }), r.attendant ? el('small', { textContent: `Atendente: ${r.attendant}` }) : null,
      r.waitingSince ? el('small', { textContent: `sem resposta desde ${quando(r.waitingSince)}` }) : null,''')

rep('''    partes.push(el('b', { textContent: 'Resposta para o cliente (você pode editar)' }), campo, el('div', { className: 'botoes' }, aprovar, humano, para, recusar));''',
'''    // O perdido já é da atendente no CRM: não há para quem passar. Recusar deixa com ela, sem mandar nada.
    const botoesDela = r.kind === 'perdido' ? [aprovar, recusar] : [aprovar, humano, para, recusar];
    if (r.kind === 'perdido' && r.decision === 'humano') partes.push(el('p', { className: 'aviso', textContent: 'A IA acha que só a atendente pode responder este cliente. Se concordar, recuse e avise a atendente.' }));
    partes.push(el('b', { textContent: 'Resposta para o cliente (você pode editar)' }), campo, el('div', { className: 'botoes' }, ...botoesDela));''')

# só busca a lista de atendentes quando houver resposta que não é de perdido
rep('''    preencherPara();
    if (!E.crmUsuarios) carregarUsuariosCrm().then(preencherPara);''','''    preencherPara();
    if (!E.crmUsuarios && r.kind !== 'perdido') carregarUsuariosCrm().then(preencherPara);''')
io.open(p,'w',encoding='utf-8',newline='').write(s)
