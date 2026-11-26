import io, re
raiz = r'f:\Projetos\Terminal-Pontual'

def patch(path, pairs, enc='utf-8'):
    s = io.open(path, encoding=enc).read()
    for old, new in pairs:
        assert s.count(old) == 1, (path, old[:80])
        s = s.replace(old, new)
    io.open(path, 'w', encoding=enc, newline='').write(s)
    return s

# ------------------------------------------------------------------ Entities
patch(raiz + r'\src\server\Pontual.Server\Data\Entities.cs', [
    ('''    /// <summary>Margem mínima do mês (R$) para o servidor entrar.</summary>
    public const string SiapeMinMargin = "siape.minMargin";
    /// <summary>Quanto a margem precisa ter crescido (R$) em relação ao mês anterior.</summary>
    public const string SiapeMinIncrease = "siape.minIncrease";
    /// <summary>Nome da tabela do mês; vazio = automático (a mais nova com telefone).</summary>
    public const string SiapeTable = "siape.table";
    /// <summary>Nome da tabela do mês anterior; vazio = automático; "-" = não comparar.</summary>
    public const string SiapePreviousTable = "siape.previousTable";
''', '''    /// <summary>Valor liberado total mínimo (R$, coluna TOTAL_LIBERADO) para o servidor entrar; o dono pediu R$ 1.000.</summary>
    public const string SiapeMinLiberado = "siape.minLiberado";
    /// <summary>Nome da tabela do mês; vazio = automático (a mais nova com telefone e valor liberado).</summary>
    public const string SiapeTable = "siape.table";
'''),
], enc='utf-8-sig')

# ------------------------------------------------------------------ SiapeMargin.cs
p = raiz + r'\src\server\Pontual.Server\Assistant\SiapeMargin.cs'
s = io.open(p, encoding='utf-8').read()

def rep(old, new):
    global s
    assert s.count(old) == 1, old[:80]
    s = s.replace(old, new)

rep('''    /// <summary>Serve como base do mês: tem CPF, margem, telefone e nome.</summary>
    public bool HasPhone => Has("cpf") && Has("Margem") && Has("TELEFONE1") && Has("nome");
    /// <summary>Serve como mês anterior (para comparar a margem): tem CPF e margem.</summary>
    public bool HasMargin => Has("cpf") && Has("Margem");
''', '''    /// <summary>Serve como base do mês: tem CPF, nome, telefone, valor liberado total e positivação.</summary>
    public bool HasPhone => Has("cpf") && Has("nome") && Has("TELEFONE1") && Has("TOTAL_LIBERADO") && Has("POSITIVACAO");
''')
rep('''/// <summary>Um servidor da base do mês com margem para oferecer.</summary>
public sealed record SiapeCandidate(string Cpf, string Name, string Phone, decimal Margin, decimal? Before, string Organ, int Age, string Uf);
''', '''/// <summary>Um servidor da base do mês com valor liberado para oferecer (<see cref="Liberado"/> = TOTAL_LIBERADO).</summary>
public sealed record SiapeCandidate(string Cpf, string Name, string Phone, decimal Liberado, decimal Margin, string Organ, int Age, string Uf);
''')
rep('''    /// <summary>
    /// Os servidores da tabela do mês com margem de pelo menos <paramref name="minMargin"/>, um por CPF, do maior ganho de
    /// margem para o menor. Com a tabela anterior, só quem ganhou pelo menos <paramref name="minIncrease"/> em relação a ela
    /// (ou não estava nela); sem ela, só a margem do mês conta.
    /// </summary>
    Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, SiapeTable? previous, decimal minMargin, decimal minIncrease, int take, CancellationToken ct);
''', '''    /// <summary>
    /// Os servidores da tabela do mês com positivação "NÃO" e valor liberado total (TOTAL_LIBERADO) de pelo menos
    /// <paramref name="minLiberado"/>, um por CPF, do maior valor para o menor.
    /// </summary>
    Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, decimal minLiberado, int take, CancellationToken ct);
''')
# consulta SQL
start = s.index('    public async Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, SiapeTable? previous')
end = s.index('        try\n        {\n            await using var connection = new SqlConnection(access.ConnectionString);\n            await connection.OpenAsync(ct);\n            await using var command = connection.CreateCommand();\n            command.CommandTimeout = 900;')
s = s[:start] + '''    public async Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, decimal minLiberado, int take, CancellationToken ct)
    {
        // O nome vem da lista do próprio servidor (sys.tables), nunca de quem usa a tela; mesmo assim só entra entre colchetes.
        static string Quoted(string name) => "[" + name.Replace("]", "]]") + "]";
        string Opt(string column, string alias) => current.Has(column) ? $"MAX({Quoted(column)}) AS {alias}" : $"'' AS {alias}";
        // Uma linha por contrato na base: agrupa por CPF. Só quem está com "NÃO" na positivação (o dono pediu) e liberado acima do mínimo.
        var sql = $"""
            SELECT TOP (@take) cpf, MAX(nome) AS nome, MAX(TELEFONE1) AS telefone, MAX(TOTAL_LIBERADO) AS liberado, {(current.Has("Margem") ? "MAX(Margem)" : "0")} AS margem,
                {Opt("NOME_ORGAO", "orgao")}, {(current.Has("IDADE") ? "MAX(IDADE)" : "0")} AS idade, {Opt("uf", "uf")}
            FROM {Quoted(current.Name)}
            WHERE cpf IS NOT NULL AND cpf <> '' AND TELEFONE1 IS NOT NULL AND LEN(TELEFONE1) >= 10
                AND UPPER(LTRIM(RTRIM(POSITIVACAO))) IN ('NAO', N'NÃO') AND TOTAL_LIBERADO >= @min
            GROUP BY cpf
            ORDER BY MAX(TOTAL_LIBERADO) DESC
            """;
''' + s[end:]
rep('''            command.Parameters.AddWithValue("@min", minMargin);
            command.Parameters.AddWithValue("@inc", minIncrease);
''', '''            command.Parameters.AddWithValue("@min", minLiberado);
''')
rep('''                list.Add(new SiapeCandidate(Text(reader, 0), Text(reader, 1), Text(reader, 2), Number(reader, 3),
                    reader.IsDBNull(4) ? null : Number(reader, 4), Text(reader, 5), age, Text(reader, 7)));
''', '''                list.Add(new SiapeCandidate(Text(reader, 0), Text(reader, 1), Text(reader, 2), Number(reader, 3), Number(reader, 4), Text(reader, 5), age, Text(reader, 7)));
''')
rep('''/// Margem liberada do SIAPE (pedido do dono, 26/11/2026): todo começo de mês a Central olha na base do SIAPE quem ganhou
/// margem (comparando a tabela do mês com a do mês anterior), a IA escreve a mensagem para cada um, como a atendente, e
/// a mensagem sai pelo CRM. Por padrão ela espera aprovação em Mensagens para aprovar; a tela pode ligar o envio direto.
''', '''/// Margem liberada do SIAPE (pedido do dono, 26/11/2026): todo começo de mês a Central olha na base do SIAPE quem tem
/// valor liberado total (TOTAL_LIBERADO) acima de R$ 1.000 e ainda está com "NÃO" na positivação; a IA escreve a mensagem
/// para cada um, como a atendente, e ela sai pelo CRM. Por padrão espera aprovação em Mensagens para aprovar.
''')
rep('''    public const decimal DefaultMinMargin = 100m;
    public const decimal DefaultMinIncrease = 50m;
''', '''    public const decimal DefaultMinLiberado = 1000m;
''')
rep('''    public sealed record Settings(bool Enabled, bool AutoSend, int Day, int Max, decimal MinMargin, decimal MinIncrease, string Table, string PreviousTable, string BotId, string Attendant)
''', '''    public sealed record Settings(bool Enabled, bool AutoSend, int Day, int Max, decimal MinLiberado, string Table, string BotId, string Attendant)
''')
rep('''                Money(values.GetValueOrDefault(TextSetting.SiapeMinMargin, ""), DefaultMinMargin),
                Money(values.GetValueOrDefault(TextSetting.SiapeMinIncrease, ""), DefaultMinIncrease),
                values.GetValueOrDefault(TextSetting.SiapeTable, ""),
                values.GetValueOrDefault(TextSetting.SiapePreviousTable, ""),
''', '''                Money(values.GetValueOrDefault(TextSetting.SiapeMinLiberado, ""), DefaultMinLiberado),
                values.GetValueOrDefault(TextSetting.SiapeTable, ""),
''')
# Choose: só a base do mês
start = s.index('    /// <summary>\n    /// A tabela do mês e a do mês anterior.')
end = s.index('    // ------------------------------------------------------------------ telefone e nome')
s = s[:start] + '''    /// <summary>
    /// A tabela do mês. Com nome escolhido na tela, vale ele; "automático" pega a base mais nova (pelo mês no nome) que tem
    /// telefone, valor liberado e positivação. Devolve também a explicação para a tela.
    /// </summary>
    public static (SiapeTable? Current, string Why) Choose(IReadOnlyList<SiapeTable> tables, string tableSetting)
    {
        var current = tableSetting.Length == 0 ? null : tables.FirstOrDefault(t => string.Equals(t.Name, tableSetting, StringComparison.OrdinalIgnoreCase));
        if (tableSetting.Length > 0 && current is null)
        {
            return (null, $"A tabela escolhida \\"{tableSetting}\\" não existe mais no banco. Escolha outra ou volte para automático.");
        }

        current ??= tables.Where(t => t.HasPhone).OrderByDescending(t => t.MonthKey ?? int.MinValue).ThenByDescending(t => t.CreatedAt).FirstOrDefault();
        if (current is null)
        {
            return (null, "Nenhuma tabela do banco tem as colunas cpf, nome, TELEFONE1, TOTAL_LIBERADO e POSITIVACAO.");
        }

        if (!current.HasPhone)
        {
            return (null, $"A tabela \\"{current.Name}\\" não tem as colunas cpf, nome, TELEFONE1, TOTAL_LIBERADO e POSITIVACAO.");
        }

        return (current, $"Base do mês: {current.Name}{(current.MonthKey is { } k ? $" ({MonthName(k)})" : "")}. Entra quem está com \\"NÃO\\" na positivação e tem valor liberado total acima do mínimo.");
    }

''' + s[end:]
# regras da IA
rep('''        Agora você escreve a PRIMEIRA mensagem para um servidor público federal (SIAPE) que ainda não conversou com a Pontual neste atendimento. A base de margens do mês mostra que ele ganhou margem consignável nova: é isso que você vai contar. Os dados dele estão em <cliente>.
''', '''        Agora você escreve a PRIMEIRA mensagem para um servidor público federal (SIAPE) que ainda não conversou com a Pontual neste atendimento. A base do mês mostra que ele tem um valor liberado para crédito consignado (margem liberada): é isso que você vai contar. Os dados dele estão em <cliente>.
''')
rep('''        - O assunto é a margem que liberou: diga que apareceu margem nova disponível no contracheque dele neste mês e que dá para aproveitar (empréstimo novo, portabilidade com troco ou refinanciamento, conforme a base). Pode citar o valor aproximado da margem liberada que está em <cliente>, arredondado; nunca invente valor de parcela, taxa ou de empréstimo.
''', '''        - O assunto é a margem liberada: diga que neste mês ele está com um valor liberado para consignado no contracheque e que dá para aproveitar (empréstimo novo, portabilidade com troco ou refinanciamento, conforme a base). Pode citar o valor liberado que está em <cliente>, arredondado para baixo ("mais de R$ 5 mil"); nunca invente valor de parcela, taxa ou prazo.
''')
rep('''        - Use "responder" com a mensagem em "mensagem". Use "nada" só se os dados em <cliente> não fizerem sentido (sem nome, margem zerada). Em "motivo", uma frase para quem vai aprovar.
''', '''        - Use "responder" com a mensagem em "mensagem". Use "nada" só se os dados em <cliente> não fizerem sentido (sem nome, valor zerado). Em "motivo", uma frase para quem vai aprovar.
''')
# ScanAsync
rep('''        var (_, tables) = await source.ProbeAsync(access, ct);
        var (current, previous, why) = Choose(tables, settings.Table, settings.PreviousTable);
        if (current is null)
        {
            return new Result(0, 0, 0, why);
        }
''', '''        var (_, tables) = await source.ProbeAsync(access, ct);
        var (current, why) = Choose(tables, settings.Table);
        if (current is null)
        {
            return new Result(0, 0, 0, why);
        }
''')
rep('''        var candidates = await source.CandidatesAsync(access, current, previous, settings.MinMargin, settings.MinIncrease, settings.Max + excluded.Count + 20, ct);
''', '''        var candidates = await source.CandidatesAsync(access, current, settings.MinLiberado, settings.Max + excluded.Count + 20, ct);
''')
rep('''            var reply = Draft(candidate, chatId, settings, current, previous, now);
''', '''            var reply = Draft(candidate, chatId, settings, current, now);
''')
rep('''            ? checkedCount == 0 ? "Nenhum servidor com margem nova (que ainda não recebeu oferta) na base do mês." : "A IA não escreveu mensagem para nenhum dos servidores encontrados."
''', '''            ? checkedCount == 0 ? "Nenhum servidor com positivação \\"NÃO\\" e valor liberado acima do mínimo (que ainda não recebeu oferta) na base do mês." : "A IA não escreveu mensagem para nenhum dos servidores encontrados."
''')
# Draft
start = s.index('    private static CrmReply Draft(SiapeCandidate c, string chatId, Settings settings, SiapeTable current, SiapeTable? previous, DateTime now)')
end = s.index('        if (c.Organ.Length > 0)\n        {\n            facts.Append(" · ").Append(c.Organ);')
s = s[:start] + '''    private static CrmReply Draft(SiapeCandidate c, string chatId, Settings settings, SiapeTable current, DateTime now)
    {
        var facts = new StringBuilder()
            .Append("Valor liberado total no SIAPE: ").Append(Money(c.Liberado))
            .Append(c.Margin != 0 ? $" (margem do mês {Money(c.Margin)})" : "").Append(" · positivação: NÃO\\n")
            .Append("CPF ").Append(NexusClient.FormatCpf(c.Cpf));
''' + s[end:]
rep('''        facts.Append("\\nBase: ").Append(current.Name);
        if (previous is not null)
        {
            facts.Append(" comparada com ").Append(previous.Name);
        }

''', '''        facts.Append("\\nBase: ").Append(current.Name);
''')
# PromptFor
start = s.index('    private static string PromptFor(SiapeCandidate c, string attendant)')
end = s.index('    /// <summary>Manda a mensagem ao cliente pela tela de conversa do CRM')
s = s[:start] + '''    private static string PromptFor(SiapeCandidate c, string attendant)
    {
        var text = new StringBuilder()
            .Append("<atendente>").Append(attendant.Length > 0 ? attendant : "não definida: escreva no jeito geral da equipe").Append("</atendente>\\n")
            .Append("<cliente>\\n")
            .Append("Nome: ").Append(Pretty(c.Name)).Append('\\n')
            .Append("Convênio: SIAPE (servidor público federal)").Append(c.Organ.Length > 0 ? $", órgão {c.Organ}" : "").Append(c.Uf.Length > 0 ? $", {c.Uf}" : "").Append('\\n');
        if (c.Age > 0)
        {
            text.Append("Idade: ").Append(c.Age).Append(" anos\\n");
        }

        text.Append("Valor liberado para consignado neste mês (margem liberada): ").Append(Money(c.Liberado)).Append('\\n');
        if (c.Margin != 0)
        {
            text.Append("Margem consignável do mês: ").Append(Money(c.Margin)).Append('\\n');
        }

        return text.Append("Ainda não foi positivado (ninguém da Pontual ofereceu este valor a ele).\\n</cliente>").ToString();
    }

''' + s[end:]
io.open(p, 'w', encoding='utf-8', newline='').write(s)

# ------------------------------------------------------------------ SiapeEndpoints.cs
p = raiz + r'\src\server\Pontual.Server\Api\SiapeEndpoints.cs'
s = io.open(p, encoding='utf-8').read()
pairs = [
    ('''    private sealed record SettingsBody(string? Server, string? Database, string? User, string? Password, string? Table, string? PreviousTable,
        decimal? MinMargin, decimal? MinIncrease, int? Max, int? Day, string? BotId, string? Attendant, bool? Enabled, bool? AutoSend);
''', '''    private sealed record SettingsBody(string? Server, string? Database, string? User, string? Password, string? Table,
        decimal? MinLiberado, int? Max, int? Day, string? BotId, string? Attendant, bool? Enabled, bool? AutoSend);
'''),
    ('''                var (current, previous, why) = SiapeMargin.Choose(list, settings.Table, settings.PreviousTable);
''', '''                var (current, why) = SiapeMargin.Choose(list, settings.Table);
'''),
    ('''                        name = t.Name, createdAt = t.CreatedAt, hasPhone = t.HasPhone, hasMargin = t.HasMargin,
                        month = t.MonthKey is { } k ? SiapeMargin.MonthName(k) : "",
                        current = t == current, previous = t == previous,
''', '''                        name = t.Name, createdAt = t.CreatedAt, hasPhone = t.HasPhone,
                        month = t.MonthKey is { } k ? SiapeMargin.MonthName(k) : "",
                        current = t == current,
'''),
    ('''            table = settings.Table, previousTable = settings.PreviousTable,
            minMargin = settings.MinMargin, minIncrease = settings.MinIncrease, max = settings.Max, day = settings.Day,
''', '''            table = settings.Table,
            minLiberado = settings.MinLiberado, max = settings.Max, day = settings.Day,
'''),
    ('''        if (body.MinMargin is < 0 || body.MinIncrease is < 0)
        {
            throw ApiError.BadRequest("A margem mínima e o aumento mínimo não podem ser negativos.");
        }
''', '''        if (body.MinLiberado is < 0)
        {
            throw ApiError.BadRequest("O valor liberado mínimo não pode ser negativo.");
        }
'''),
    ('''        await SetAsync(db, TextSetting.SiapeTable, Input.Truncated(body.Table, 128).Trim(), ct);
        await SetAsync(db, TextSetting.SiapePreviousTable, Input.Truncated(body.PreviousTable, 128).Trim(), ct);
        await SetAsync(db, TextSetting.SiapeMinMargin, (body.MinMargin ?? SiapeMargin.DefaultMinMargin).ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, TextSetting.SiapeMinIncrease, (body.MinIncrease ?? SiapeMargin.DefaultMinIncrease).ToString(CultureInfo.InvariantCulture), ct);
''', '''        await SetAsync(db, TextSetting.SiapeTable, Input.Truncated(body.Table, 128).Trim(), ct);
        await SetAsync(db, TextSetting.SiapeMinLiberado, (body.MinLiberado ?? SiapeMargin.DefaultMinLiberado).ToString(CultureInfo.InvariantCulture), ct);
'''),
    ('''            var (_, _, why) = SiapeMargin.Choose(tables, settings.Table, settings.PreviousTable);
''', '''            var (_, why) = SiapeMargin.Choose(tables, settings.Table);
'''),
]
for old, new in pairs:
    assert s.count(old) == 1, old[:80]
    s = s.replace(old, new)
io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('ok servidor')
