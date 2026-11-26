import io
p = r'f:\Projetos\Terminal-Pontual\src\server\Pontual.Server\wwwroot\central\neural\siape.js'
s = io.open(p, encoding='utf-8').read()
pairs = [
    ("// na máquina da Kamila), acha quem ganhou margem em relação ao mês anterior, a IA escreve a mensagem como a atendente e\n",
     "// na máquina da Kamila), acha quem tem valor liberado total acima de R$ 1.000 e positivação \"NÃO\", a IA escreve como a atendente e\n"),
    ("    table: v('siape-tabela'), previousTable: v('siape-anterior'),\n    minMargin: Number(v('siape-min')), minIncrease: Number(v('siape-aumento')), max: Number(v('siape-max')), day: Number(v('siape-dia')),\n",
     "    table: v('siape-tabela'),\n    minLiberado: Number(v('siape-liberado')), max: Number(v('siape-max')), day: Number(v('siape-dia')),\n"),
    ("    { aba: 'bases', titulo: 'Bases comparadas', estado: s.chosen ? 'ok' : s.tables ? 'erro' : 'falta',\n",
     "    { aba: 'bases', titulo: 'Base do mês', estado: s.chosen ? 'ok' : s.tables ? 'erro' : 'falta',\n"),
    ("""  const anterior = el('select', { className: 'campo', id: 'siape-anterior' },
    opcao('', 'Automático: a base do mês anterior', s.previousTable === ''),
    opcao('-', 'Não comparar: vale só a margem do mês', s.previousTable === '-'),
    ...tabelas.filter((t) => t.hasMargin).map((t) => opcao(t.name, nomeTabela(t), s.previousTable === t.name)));
  if (s.previousTable && s.previousTable !== '-' && !tabelas.some((t) => t.name === s.previousTable)) anterior.append(opcao(s.previousTable, `${s.previousTable} (não está no banco)`, true));
  const min = el('input', { className: 'campo', id: 'siape-min', type: 'number', min: '0', step: '10', value: s.minMargin });
  const aumento = el('input', { className: 'campo', id: 'siape-aumento', type: 'number', min: '0', step: '10', value: s.minIncrease });
""", """  const liberado = el('input', { className: 'campo', id: 'siape-liberado', type: 'number', min: '0', step: '100', value: s.minLiberado });
"""),
    ("""    el('div', { className: 'siape-grupo' }, titulinho('Quais bases comparar'),
      el('p', { className: 'siape-explica', textContent: s.chosen || 'A Central escolhe sozinha a base mais nova com telefone e a compara com a do mês anterior. Dá para escolher à mão.' }),
      el('div', { className: 'siape-grade' }, campoRotulo('Base do mês', tabela), campoRotulo('Base do mês anterior', anterior))),
    el('div', { className: 'siape-grupo' }, titulinho('Quem entra'),
      el('p', { className: 'siape-explica', textContent: 'Entra quem ganhou margem de um mês para o outro. Um por CPF, do maior ganho para o menor, só celulares, e ninguém recebe duas ofertas em cinco meses.' }),
      el('div', { className: 'siape-grade' },
        campoRotulo('Margem mínima do mês (R$)', min, 'Abaixo disso não vale a pena oferecer.'),
        campoRotulo('Aumento mínimo da margem (R$)', aumento, 'Quanto a margem precisa ter crescido em relação ao mês anterior.'))),
""", """    el('div', { className: 'siape-grupo' }, titulinho('Qual base ler'),
      el('p', { className: 'siape-explica', textContent: s.chosen || 'A Central escolhe sozinha a base mais nova que tem telefone, valor liberado e positivação. Dá para escolher à mão.' }),
      el('div', { className: 'siape-grade' }, campoRotulo('Base do mês', tabela))),
    el('div', { className: 'siape-grupo' }, titulinho('Quem entra'),
      el('p', { className: 'siape-explica', textContent: 'Entra quem está com "NÃO" na positivação e tem valor liberado total (coluna TOTAL_LIBERADO) acima do mínimo. Um por CPF, do maior valor para o menor, só celulares, e ninguém recebe duas ofertas em cinco meses.' }),
      el('div', { className: 'siape-grade' },
        campoRotulo('Valor liberado mínimo (R$)', liberado, 'Pedido da chefia: só quem tem mais de R$ 1.000 liberado.'))),
"""),
    ("""        el('li', { textContent: 'A Kamila sobe a base nova no SQL todo mês. A Central escolhe sozinha a tabela mais nova que tem telefone e a compara com a do mês anterior (dá para escolher à mão).' }),
        el('li', { textContent: 'Entra quem tem margem acima do mínimo e ganhou pelo menos o aumento mínimo em relação ao mês anterior (ou é novo na base). Só celulares; um por CPF; quem já recebeu oferta nos últimos cinco meses fica de fora.' }),
        el('li', { textContent: 'A IA escreve a primeira mensagem como a atendente: se apresenta pela Pontual, conta que liberou margem e convida para uma simulação. Nunca inventa parcela ou taxa.' }),
""", """        el('li', { textContent: 'A Kamila sobe a base nova no SQL todo mês. A Central escolhe sozinha a tabela mais nova que tem telefone, valor liberado e positivação (dá para escolher à mão).' }),
        el('li', { textContent: 'Entra quem está com "NÃO" na positivação e tem valor liberado total acima do mínimo (R$ 1.000). Só celulares; um por CPF; do maior valor para o menor; quem já recebeu oferta nos últimos cinco meses fica de fora.' }),
        el('li', { textContent: 'A IA escreve a primeira mensagem como a atendente: se apresenta pela Pontual, conta que ele está com valor liberado (margem liberada) e convida para uma simulação. Nunca inventa parcela ou taxa.' }),
"""),
]
for old, new in pairs:
    assert s.count(old) == 1, old[:80]
    s = s.replace(old, new)
assert 'hasMargin' not in s and 'previousTable' not in s and 'minMargin' not in s, 'sobrou referência antiga'
io.open(p, 'w', encoding='utf-8', newline='').write(s)

# testes
p = r'f:\Projetos\Terminal-Pontual\tests\server\Pontual.Server.Tests\Api\SiapeTests.cs'
s = io.open(p, encoding='utf-8').read()
pairs = [
    ('''            new("COMPLETA_NOVEMBRO_CORRIGIDA", new DateTime(2026, 11, 25), ["banco", "nome", "cpf", "Margem", "TELEFONE1", "NOME_ORGAO", "IDADE", "uf"]),
            new("COMPLETA_OUTUBRO_REFEITA", new DateTime(2026, 11, 11), ["banco", "nome", "cpf", "Margem", "NOME_ORGAO", "IDADE"]),
''', '''            new("COMPLETA_NOVEMBRO_CORRIGIDA", new DateTime(2026, 11, 25), ["banco", "nome", "cpf", "Margem", "TELEFONE1", "NOME_ORGAO", "IDADE", "uf", "TOTAL_LIBERADO", "POSITIVACAO"]),
            new("COMPLETA_OUTUBRO_REFEITA", new DateTime(2026, 11, 11), ["banco", "nome", "cpf", "Margem", "NOME_ORGAO", "IDADE", "TOTAL_LIBERADO", "POSITIVACAO"]),
'''),
    ('''        public List<(string Current, string? Previous, decimal Min, decimal Inc, int Take)> Asked { get; } = [];
''', '''        public List<(string Current, decimal Min, int Take)> Asked { get; } = [];
'''),
    ('''        public Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, SiapeTable? previous, decimal minMargin, decimal minIncrease, int take, CancellationToken ct)
        {
            Asked.Add((current.Name, previous?.Name, minMargin, minIncrease, take));
            return Task.FromResult<IReadOnlyList<SiapeCandidate>>(Candidates.Where(c => c.Margin >= minMargin).Take(take).ToList());
        }
''', '''        public Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, decimal minLiberado, int take, CancellationToken ct)
        {
            Asked.Add((current.Name, minLiberado, take));
            return Task.FromResult<IReadOnlyList<SiapeCandidate>>(Candidates.Where(c => c.Liberado >= minLiberado).OrderByDescending(c => c.Liberado).Take(take).ToList());
        }
'''),
    ('''            minMargin = 100, minIncrease = 50, max = 3, day = 1, botId = "Siape_6", attendant = "Kamila", enabled = true, autoSend,
''', '''            minLiberado = 1000, max = 3, day = 1, botId = "Siape_6", attendant = "Kamila", enabled = true, autoSend,
'''),
    ('''        Assert.StartsWith("Base do mês: COMPLETA_NOVEMBRO_CORRIGIDA", saved.GetProperty("chosen").GetString());
        Assert.Contains("COMPLETA_OUTUBRO_REFEITA", saved.GetProperty("chosen").GetString());
''', '''        Assert.StartsWith("Base do mês: COMPLETA_NOVEMBRO_CORRIGIDA", saved.GetProperty("chosen").GetString());
        Assert.Contains("positivação", saved.GetProperty("chosen").GetString());
'''),
    ('''    public void Le_o_mes_pelo_nome_da_tabela_e_escolhe_a_base_e_a_anterior()
''', '''    public void Le_o_mes_pelo_nome_da_tabela_e_escolhe_a_base_do_mes()
'''),
    ('''        var tables = new FakeSiape().Tables;
        var (current, previous, _) = SiapeMargin.Choose(tables, "", "");
        Assert.Equal(("COMPLETA_NOVEMBRO_CORRIGIDA", "COMPLETA_OUTUBRO_REFEITA"), (current!.Name, previous!.Name));

        // Escolhas da tela valem; "-" é não comparar.
        var (c2, p2, _) = SiapeMargin.Choose(tables, "COMPLETA_NOVEMBRO_CORRIGIDA", "-");
        Assert.Equal("COMPLETA_NOVEMBRO_CORRIGIDA", c2!.Name);
        Assert.Null(p2);
        var (c3, _, why) = SiapeMargin.Choose(tables, "SUMIU", "");
        Assert.Null(c3);
        Assert.Contains("não existe mais", why);
''', '''        var tables = new FakeSiape().Tables;
        var (current, _) = SiapeMargin.Choose(tables, "");
        Assert.Equal("COMPLETA_NOVEMBRO_CORRIGIDA", current!.Name);

        // A escolha da tela vale; a de outubro não tem telefone e é recusada.
        var (c2, why2) = SiapeMargin.Choose(tables, "COMPLETA_OUTUBRO_REFEITA");
        Assert.Null(c2);
        Assert.Contains("TELEFONE1", why2);
        var (c3, why) = SiapeMargin.Choose(tables, "SUMIU");
        Assert.Null(c3);
        Assert.Contains("não existe mais", why);
'''),
    ('''    public async Task Busca_quem_ganhou_margem_escreve_como_a_atendente_e_espera_aprovacao()
''', '''    public async Task Busca_quem_tem_valor_liberado_escreve_como_a_atendente_e_espera_aprovacao()
'''),
    ('''            new("65500000001", "LUCINEA FERREIRA", "24999999967", 1800m, 900m, "MINISTERIO DA SAUDE", 52, "RJ"),
            new("65500000002", "JOAO FIXO", "2133334444", 1500m, 200m, "INSS", 60, "SP"), // telefone fixo: fora
            new("65500000003", "ANA NOVA", "61982378969", 700m, null, "", 0, "DF"),
''', '''            new("65500000001", "LUCINEA FERREIRA", "24999999967", 18500m, 900m, "MINISTERIO DA SAUDE", 52, "RJ"),
            new("65500000002", "JOAO FIXO", "2133334444", 15000m, 200m, "INSS", 60, "SP"), // telefone fixo: fora
            new("65500000003", "ANA NOVA", "61982378969", 2300m, 0m, "", 0, "DF"),
            new("65500000004", "POUCO", "61982378900", 800m, 0m, "", 0, "DF"), // abaixo de R$ 1.000: fora
'''),
    ('''        // A base lida foi a do mês comparada com a anterior, com as regras da tela.
        var asked = Assert.Single(siape.Asked);
        Assert.Equal(("COMPLETA_NOVEMBRO_CORRIGIDA", "COMPLETA_OUTUBRO_REFEITA", 100m, 50m), (asked.Current, asked.Previous, asked.Min, asked.Inc));
''', '''        // A base lida foi a do mês, com o mínimo da tela.
        var asked = Assert.Single(siape.Asked);
        Assert.Equal(("COMPLETA_NOVEMBRO_CORRIGIDA", 1000m), (asked.Current, asked.Min));
'''),
    ('''        Assert.Contains("liberou R$ 900,00 a mais", model.Requests[0].Input.Replace('\\u00a0', ' '));
        Assert.Contains("toda a margem é nova", model.Requests[1].Input);
''', '''        Assert.Contains("Valor liberado para consignado neste mês (margem liberada): R$ 18.500,00", model.Requests[0].Input.Replace('\\u00a0', ' '));
        Assert.Contains("Ainda não foi positivado", model.Requests[1].Input);
'''),
    ('''        Assert.Contains("Margem liberada no SIAPE", lucinea.GetProperty("incoming").GetString());
''', '''        Assert.Contains("Valor liberado total no SIAPE: R$ 18.500,00", lucinea.GetProperty("incoming").GetString()!.Replace('\\u00a0', ' '));
'''),
    ('''            siape.Candidates.Add(new($"6550000000{i}", $"SERVIDOR {i}", $"6198237896{i}", 2000m - i * 100, 500m, "ORGAO", 45, "DF"));
''', '''            siape.Candidates.Add(new($"6550000000{i}", $"SERVIDOR {i}", $"6198237896{i}", 20000m - i * 100, 500m, "ORGAO", 45, "DF"));
'''),
    ('''        siape.Candidates.Add(new("65500000001", "LUCINEA", "24999999967", 1800m, 900m, "", 0, ""));
''', '''        siape.Candidates.Add(new("65500000001", "LUCINEA", "24999999967", 1800m, 0m, "", 0, ""));
'''),
]
for old, new in pairs:
    assert s.count(old) == 1, old[:80]
    s = s.replace(old, new)
io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('ok ui+testes')
