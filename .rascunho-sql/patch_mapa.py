import io, os
raiz = r'f:\Projetos\Terminal-Pontual'

def patch(path, pairs, enc='utf-8'):
    s = io.open(path, encoding=enc).read()
    for old, new in pairs:
        assert s.count(old) == 1, (path, old[:70])
        s = s.replace(old, new)
    io.open(path, 'w', encoding=enc, newline='').write(s)

# ---------------------------------------------------------------- servidor: /neural traz as mensagens de margem do mês
ne = raiz + r'\src\server\Pontual.Server\Api\NeuralEndpoints.cs'
old = "        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);\n        var enabledChars = items.Where(i => i.enabled).Sum(i => (long)i.chars);\n"
new = old + '''
        // Margem liberada do SIAPE: as mensagens do mês (de Brasília), para o ramo do mapa.
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, AssistantPrompt.BrazilTime);
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(new DateTime(local.Year, local.Month, 1), DateTimeKind.Unspecified), AssistantPrompt.BrazilTime);
        var siapeMonth = await db.CrmReplies.AsNoTracking().Where(r => r.Kind == SiapeMargin.Kind && r.CreatedAt >= monthStart).OrderByDescending(r => r.CreatedAt).Take(300)
            .Select(r => new { id = r.Id, contact = r.Contact, phone = r.Phone, status = r.Status, reply = r.Reply, incoming = r.Incoming, reason = r.Reason, error = r.Error, createdAt = r.CreatedAt, sentAt = r.SentAt })
            .ToListAsync(ct);
        var siapeSettings = SiapeMargin.Settings.From(settings);
'''
old2 = "            limits = new { item = KnowledgeLimits.Item, upload = KnowledgeLimits.Upload, extensions = DocumentText.Extensions, title = MaxTitle, manual = AssistantEndpoints.MaxGeneralInstructions },\n        });\n    }\n"
new2 = '''            limits = new { item = KnowledgeLimits.Item, upload = KnowledgeLimits.Upload, extensions = DocumentText.Extensions, title = MaxTitle, manual = AssistantEndpoints.MaxGeneralInstructions },
            siape = new
            {
                configured = SiapeMargin.Access(settings) is not null,
                enabled = siapeSettings.Enabled,
                autoSend = siapeSettings.AutoSend,
                table = siapeSettings.Table,
                month = siapeMonth,
            },
        });
    }
'''
patch(ne, [(old, new), (old2, new2)], enc='utf-8-sig')

# ---------------------------------------------------------------- mapa.js: cor do ramo e chave nova das posições (o desenho mudou)
mj = raiz + r'\src\server\Pontual.Server\wwwroot\central\neural\mapa.js'
patch(mj, [
    ("  attendant: '#3ddc97', suggestion: '#ffb454', manual: '#ff8fb8', erro: '#ff6b7d',\n",
     "  attendant: '#3ddc97', suggestion: '#ffb454', manual: '#ff8fb8', siape: '#7be0d6', erro: '#ff6b7d',\n"),
    ("localStorage.getItem('neural.mapa.posicoes.v3')", "localStorage.getItem('neural.mapa.posicoes.v4')"),
])
s = io.open(mj, encoding='utf-8').read()
assert "neural.mapa.posicoes.v3" not in s.replace("getItem('neural.mapa.posicoes.v4')", ''), 'ainda há v3 em outro lugar'

# ---------------------------------------------------------------- neural.js: o ramo no grafo, filtro, dica, gaveta
nj = raiz + r'\src\server\Pontual.Server\wwwroot\central\neural\neural.js'
tipos_old = "  suggestion: { um: 'Sugestão pendente', varios: 'Aprendizado', cor: MAPA_CORES.suggestion },\n"
tipos_new = tipos_old + "  siape: { um: 'Margem liberada', varios: 'Margem liberada (SIAPE)', cor: MAPA_CORES.siape },\n"
grafo_old = "  nos.push({ id: 'm:manual', pai: 'r:manual', tipo: 'manual', ramo: 'manual', rotulo: 'Comportamento e comunicação com o cliente', estado: manual ? 'active' : 'disabled' });\n  return { nos, ligacoes: d.links.map((l) => ({ a: l.from, b: l.to })) };\n"
grafo_new = '''  nos.push({ id: 'm:manual', pai: 'r:manual', tipo: 'manual', ramo: 'manual', rotulo: 'Comportamento e comunicação com o cliente', estado: manual ? 'active' : 'disabled' });
  // Margem liberada do SIAPE: a base no SQL e as mensagens que a IA escreveu neste mês, por situação.
  const sp = d.siape || { month: [] }, mes = sp.month || [];
  nos.push({ id: 'r:siape', pai: 'ia', tipo: 'ramo', ramo: 'siape', rotulo: 'Margem liberada', n: mes.length + 1 });
  nos.push({ id: 'z:base', pai: 'r:siape', tipo: 'siape', ramo: 'siape', rotulo: 'Base de margens do SIAPE (SQL)', estado: sp.configured ? (sp.enabled ? 'active' : 'pending') : 'disabled' });
  const grupoDe = (m) => (m.status === 'pending' || m.status === 'failed' ? 'pending' : m.status === 'sent' ? 'sent' : 'other');
  for (const [g, rotulo] of [['pending', 'Esperando aprovação'], ['sent', 'Enviadas este mês'], ['other', 'Recusadas e outras']]) {
    const n = mes.filter((m) => grupoDe(m) === g).length;
    if (n) nos.push({ id: `g:siape:${g}`, pai: 'r:siape', tipo: 'grupo', ramo: 'siape', rotulo, n });
  }
  for (const m of mes) nos.push({ id: `z:${m.id}`, pai: `g:siape:${grupoDe(m)}`, tipo: 'siape', ramo: 'siape', rotulo: m.contact || m.phone, estado: grupoDe(m) === 'pending' ? 'pending' : grupoDe(m) === 'sent' ? 'active' : 'disabled', novo: grupoDe(m) === 'sent' && recente(m.sentAt) });
  return { nos, ligacoes: d.links.map((l) => ({ a: l.from, b: l.to })) };
'''
chip_old = "attendant: d.attendants.length, suggestion: d.suggestions.pending, manual: d.settings.manual ? 1 : 0 };\n"
chip_new = "attendant: d.attendants.length, suggestion: d.suggestions.pending, manual: d.settings.manual ? 1 : 0, siape: (d.siape?.month || []).length };\n"
dica_old = "  else if (no.tipo === 'manual') linhas.push('Como a IA se comporta e escreve para o cliente', no.estado === 'active' ? 'Escrito e em uso em toda resposta' : 'Ainda não escrito');\n"
dica_new = dica_old + "  else if (no.tipo === 'siape') linhas.push(no.id === 'z:base' ? 'A base de margens no SQL Server da Kamila' : 'Mensagem de margem liberada', no.id === 'z:base' ? (no.estado === 'active' ? 'Ligada: a busca roda todo mês' : no.estado === 'pending' ? 'Configurada, busca mensal desligada' : 'Ligação ainda não configurada') : no.estado === 'pending' ? 'Esperando aprovação' : no.estado === 'active' ? 'Enviada pelo CRM' : 'Recusada ou não enviada');\n"
sel_old = "  else if (no.tipo === 'manual') gavetaManual();\n  else gavetaItem(no.id);\n"
sel_new = "  else if (no.tipo === 'manual') gavetaManual();\n  else if (no.tipo === 'siape') gavetaMargem(no.id.slice(2));\n  else gavetaItem(no.id);\n"
acao_old = "  if (no.id === 'r:manual') { acao = botao(E.d.settings.manual ? 'Editar o manual' : 'Escrever o manual', 'editar', 'btn mini'); acao.onclick = abrirManual; }\n"
acao_new = acao_old + "  if (no.id === 'r:siape' || no.id.startsWith('g:siape:')) { acao = botao(no.id === 'g:siape:pending' ? 'Aprovar as mensagens' : 'Abrir Margem liberada', 'raio', 'btn mini'); acao.onclick = () => ir(no.id === 'g:siape:pending' ? 'aprovar' : 'siape'); }\n"
vazio_old = "'r:manual': 'O manual ainda não foi escrito.' };\n"
vazio_new = "'r:manual': 'O manual ainda não foi escrito.', 'r:siape': 'Nenhuma mensagem de margem liberada neste mês.' };\n"
gaveta_old = "async function gavetaItem(id) {\n"
gaveta_new = '''// Uma mensagem de margem liberada do SIAPE (ou a própria base): o resumo e o atalho para a aba.
function gavetaMargem(id) {
  const sp = E.d.siape || { month: [] };
  const abrir = botao('Abrir Margem liberada', 'raio', 'btn mini'); abrir.onclick = () => ir('siape');
  if (id === 'base') {
    gaveta('z:base', topoGaveta(selo('siape liso', 'Base')), el('h2', { textContent: 'Base de margens do SIAPE' }),
      el('p', { className: 'frase', textContent: 'O SQL Server da Kamila com a base de margens dos servidores federais. Todo mês a Central lê a base nova, compara com a anterior e a IA escreve para quem ganhou margem.' }),
      el('dl', { className: 'dados' }, dado('Ligação', sp.configured ? 'Configurada' : 'Não configurada'), dado('Busca mensal', sp.enabled ? 'Ligada' : 'Desligada'), dado('Envio', sp.autoSend ? 'Direto, sem aprovação' : 'Com aprovação'), dado('Base do mês', sp.table || 'Automática (a mais nova com telefone)'), dado('Mensagens neste mês', mil(sp.month.length))),
      el('div', { className: 'botoes' }, abrir));
    return;
  }
  const m = sp.month.find((x) => x.id === id);
  if (!m) { fecharGaveta(); return; }
  const [rotulo, classe] = SITUACAO_CRM[m.status] || [m.status, 'liso'];
  const aprovar = botao('Aprovar na fila', 'balao', 'btn mini'); aprovar.onclick = () => ir('aprovar');
  gaveta(`z:${id}`, topoGaveta(selo('siape liso', 'Margem liberada'), selo(classe, rotulo)), el('h2', { textContent: m.contact || m.phone }),
    el('p', { className: 'frase', textContent: m.phone }),
    el('dl', { className: 'dados' }, dado('Escrita', quando(m.createdAt)), dado('Enviada', m.sentAt ? quando(m.sentAt) : 'Ainda não')),
    secao('Dados da base do SIAPE', el('p', { className: 'citacao', style: 'white-space:pre-wrap', textContent: m.incoming })),
    m.reply ? secao('Mensagem da IA', el('p', { className: 'citacao', style: 'white-space:pre-wrap', textContent: m.reply })) : null,
    m.reason ? el('p', { className: 'nota', textContent: `Motivo da IA: ${m.reason}` }) : null,
    m.error ? el('p', { className: 'erro', textContent: m.error }) : null,
    el('div', { className: 'botoes' }, m.status === 'pending' || m.status === 'failed' ? aprovar : null, abrir));
}

async function gavetaItem(id) {
'''
patch(nj, [(tipos_old, tipos_new), (grafo_old, grafo_new), (chip_old, chip_new), (dica_old, dica_new), (sel_old, sel_new), (acao_old, acao_new), (vazio_old, vazio_new), (gaveta_old, gaveta_new)])

# ---------------------------------------------------------------- página de teste com a estrutura real + o ramo novo
rh = raiz + r'\.rascunho-central-neural\mapa-teste\real.html'
r_old = "add('r:manual','ia','ramo','Manual de comunicação',{ramo:'manual'});add('m:manual','r:manual','manual','Comportamento',{ramo:'manual'});\n"
r_new = r_old + "add('r:siape','ia','ramo','Margem liberada',{ramo:'siape',n:43});add('z:base','r:siape','siape','Base de margens do SIAPE (SQL)',{ramo:'siape'});\nramoSiape();\n"
r2_old = "const m=new MapaNeural(document.getElementById('c'));m.setDados({nos});\n"
r2_new = "function ramoSiape(){const gs=[['g:siape:pending','Esperando aprovação',12,'pending'],['g:siape:sent','Enviadas este mês',30,'active']];for(const [g,rot,n,est] of gs){add(g,'r:siape','grupo',rot,{ramo:'siape',n});for(let i=0;i<n;i++)add('z:'+g+i,g,'siape','Servidor '+i,{ramo:'siape',estado:est});}}\n" + r2_old
patch(rh, [(r_old, r_new), (r2_old, r2_new)])
print('ok')
