import io
root = r"f:/Projetos/Terminal-Pontual/"
def patch(path, pairs):
    p = root + path
    s = io.open(p, encoding="utf-8", newline="").read()
    for old, new in pairs:
        assert s.count(old) == 1, (path, old[:70], s.count(old))
        s = s.replace(old, new)
    io.open(p, "w", encoding="utf-8", newline="").write(s)
    print("ok", path)

patch("src/server/Pontual.Server/Assistant/SiapeMargin.cs", [
("""/// <summary>Um servidor da base do mês com valor liberado para oferecer (<see cref="Liberado"/> = TOTAL_LIBERADO).</summary>
public sealed record SiapeCandidate(string Cpf, string Name, string Phone, decimal Liberado, decimal Margin, string Organ, int Age, string Uf);
""",
"""/// <summary>Um contrato do servidor na base: o banco e o valor liberado nele (colunas <c>banco</c> e <c>VALOR_LIBERADO</c>).</summary>
public sealed record SiapeContract(string Bank, decimal Liberado);

/// <summary>
/// Um servidor da base do mês com valor liberado para oferecer (<see cref="Liberado"/> = TOTAL_LIBERADO, <see cref="Refund"/> = REEMBOLSO,
/// e um contrato por banco em <see cref="Contracts"/>). O dono pediu (26/11/2026) que a mensagem traga nome, valor liberado por banco,
/// total liberado e reembolso.
/// </summary>
public sealed record SiapeCandidate(string Cpf, string Name, string Phone, decimal Liberado, decimal Margin, string Organ, int Age, string Uf,
    decimal Refund = 0, IReadOnlyList<SiapeContract>? Contracts = null)
{
    public IReadOnlyList<SiapeContract> Contracts { get; init; } = Contracts ?? [];
}
"""),
("""        string Opt(string column, string alias) => current.Has(column) ? $"MAX({Quoted(column)}) AS {alias}" : $"'' AS {alias}";
        // Uma linha por contrato na base: agrupa por CPF. Só quem está com "NÃO" na positivação (o dono pediu) e liberado acima do mínimo.
        var sql = $\"\"\"
            SELECT TOP (@take) cpf, MAX(nome) AS nome, MAX(TELEFONE1) AS telefone, MAX(TOTAL_LIBERADO) AS liberado, {(current.Has("Margem") ? "MAX(Margem)" : "0")} AS margem,
                {Opt("NOME_ORGAO", "orgao")}, {(current.Has("IDADE") ? "MAX(IDADE)" : "0")} AS idade, {Opt("uf", "uf")}
            FROM {Quoted(current.Name)}
            WHERE cpf IS NOT NULL AND cpf <> '' AND TELEFONE1 IS NOT NULL AND LEN(TELEFONE1) >= 10
                AND UPPER(LTRIM(RTRIM(POSITIVACAO))) IN ('NAO', N'NÃO') AND TOTAL_LIBERADO >= @min
            GROUP BY cpf
            ORDER BY MAX(TOTAL_LIBERADO) DESC
            \"\"\";
""",
"""        string Col(string column, string fallback) => current.Has(column) ? Quoted(column) : fallback;
        // Uma linha por contrato na base (banco + VALOR_LIBERADO); TOTAL_LIBERADO e REEMBOLSO repetem em todas as linhas do CPF.
        // Primeiro escolhe os CPFs (só quem está com "NÃO" na positivação e liberado acima do mínimo), depois traz os contratos deles.
        var sql = $\"\"\"
            WITH base AS (
                SELECT cpf, nome, TELEFONE1, TOTAL_LIBERADO, {Col("Margem", "0")} AS margem, {Col("NOME_ORGAO", "''")} AS orgao, {Col("IDADE", "0")} AS idade, {Col("uf", "''")} AS uf,
                    {Col("REEMBOLSO", "0")} AS reembolso, {Col("banco", "''")} AS banco, {Col("VALOR_LIBERADO", "0")} AS valor
                FROM {Quoted(current.Name)}
                WHERE cpf IS NOT NULL AND cpf <> '' AND TELEFONE1 IS NOT NULL AND LEN(TELEFONE1) >= 10
                    AND UPPER(LTRIM(RTRIM(POSITIVACAO))) IN ('NAO', N'NÃO') AND TOTAL_LIBERADO >= @min),
            escolhidos AS (SELECT TOP (@take) cpf, MAX(TOTAL_LIBERADO) AS liberado FROM base GROUP BY cpf ORDER BY MAX(TOTAL_LIBERADO) DESC)
            SELECT b.cpf, b.nome, b.TELEFONE1, e.liberado, b.margem, b.orgao, b.idade, b.uf, b.reembolso, b.banco, b.valor
            FROM base b JOIN escolhidos e ON e.cpf = b.cpf
            ORDER BY e.liberado DESC, b.cpf, b.valor DESC
            \"\"\";
"""),
("""            var list = new List<SiapeCandidate>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                static string Text(SqlDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
                static decimal Number(SqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToDecimal(r.GetValue(i), CultureInfo.InvariantCulture);
                var age = reader.IsDBNull(6) ? 0 : (int)Math.Clamp(Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture), 0, 130);
                list.Add(new SiapeCandidate(Text(reader, 0), Text(reader, 1), Text(reader, 2), Number(reader, 3), Number(reader, 4), Text(reader, 5), age, Text(reader, 7)));
            }

            return list;
""",
"""            var list = new List<SiapeCandidate>();
            var contracts = new List<SiapeContract>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                static string Text(SqlDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
                static decimal Number(SqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToDecimal(r.GetValue(i), CultureInfo.InvariantCulture);
                var cpf = Text(reader, 0);
                if (list.Count == 0 || list[^1].Cpf != cpf)
                {
                    contracts = [];
                    var age = reader.IsDBNull(6) ? 0 : (int)Math.Clamp(Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture), 0, 130);
                    list.Add(new SiapeCandidate(cpf, Text(reader, 1), Text(reader, 2), Number(reader, 3), Number(reader, 4), Text(reader, 5), age, Text(reader, 7), Number(reader, 8), contracts));
                }

                // Um contrato por linha; só os com valor liberado positivo interessam na mensagem.
                var value = Number(reader, 10);
                if (value > 0)
                {
                    contracts.Add(new SiapeContract(Text(reader, 9), value));
                }
            }

            return list;
"""),
("""        - O assunto é o valor liberado: diga que neste mês ele está com um valor liberado para consignado no contracheque e que dá para aproveitar (empréstimo novo, portabilidade com troco ou refinanciamento, conforme a base). Pode citar o valor liberado que está em <cliente>, arredondado para baixo ("mais de R$ 5 mil"); nunca invente valor de parcela, taxa ou prazo.
""",
"""        - O assunto é o valor liberado: diga que neste mês ele está com um valor liberado para consignado no contracheque e que dá para aproveitar (empréstimo novo, portabilidade com troco ou refinanciamento, conforme a base).
        - A mensagem TEM QUE trazer, exatamente como estão em <cliente>: o nome do cliente, cada banco com o valor liberado nele, o total liberado e o reembolso. Copie os valores como estão (R$ com centavos), um banco por linha quando houver mais de um. Fora isso, nunca invente valor de parcela, taxa ou prazo.
"""),
("""        var facts = new StringBuilder()
            .Append("Valor liberado total no SIAPE: ").Append(Money(c.Liberado))
            .Append(c.Margin != 0 ? $" (margem do mês {Money(c.Margin)})" : "").Append(" · positivação: NÃO\\n")
            .Append("CPF ").Append(NexusClient.FormatCpf(c.Cpf));
""",
"""        var facts = new StringBuilder()
            .Append("Valor liberado total no SIAPE: ").Append(Money(c.Liberado))
            .Append(c.Refund != 0 ? $" · reembolso {Money(c.Refund)}" : "")
            .Append(c.Margin != 0 ? $" (margem do mês {Money(c.Margin)})" : "").Append(" · positivação: NÃO\\n");
        foreach (var contract in c.Contracts)
        {
            facts.Append(contract.Bank.Length > 0 ? contract.Bank : "Banco não informado").Append(": ").Append(Money(contract.Liberado)).Append('\\n');
        }

        facts.Append("CPF ").Append(NexusClient.FormatCpf(c.Cpf));
"""),
("""        text.Append("Valor liberado para consignado neste mês: ").Append(Money(c.Liberado)).Append('\\n');
        if (c.Margin != 0)
""",
"""        text.Append("Total liberado para consignado neste mês: ").Append(Money(c.Liberado)).Append('\\n');
        text.Append("Reembolso: ").Append(Money(c.Refund)).Append('\\n');
        if (c.Contracts.Count > 0)
        {
            text.Append("Valor liberado por banco:\\n");
            foreach (var contract in c.Contracts)
            {
                text.Append("- ").Append(contract.Bank.Length > 0 ? Pretty(contract.Bank) : "Banco não informado").Append(": ").Append(Money(contract.Liberado)).Append('\\n');
            }
        }

        if (c.Margin != 0)
"""),
])

patch("src/server/Pontual.Server/wwwroot/central/neural/siape.js", [
("conta que ele está com valor liberado e convida para uma simulação. Nunca inventa parcela ou taxa.",
 "escreve o nome do cliente, o valor liberado em cada banco, o total liberado e o reembolso (como estão na base) e convida para uma simulação. Nunca inventa parcela ou taxa."),
])

NB = '\u00a0'
patch("tests/server/Pontual.Server.Tests/Api/SiapeTests.cs", [
('["banco", "nome", "cpf", "Margem", "TELEFONE1", "NOME_ORGAO", "IDADE", "uf", "TOTAL_LIBERADO", "POSITIVACAO"]),',
 '["banco", "nome", "cpf", "Margem", "TELEFONE1", "NOME_ORGAO", "IDADE", "uf", "TOTAL_LIBERADO", "POSITIVACAO", "VALOR_LIBERADO", "REEMBOLSO"]),'),
('new("65500000001", "LUCINEA FERREIRA", "24999999967", 18500m, 900m, "MINISTERIO DA SAUDE", 52, "RJ"),',
 'new("65500000001", "LUCINEA FERREIRA", "24999999967", 18500m, 900m, "MINISTERIO DA SAUDE", 52, "RJ", 1234.56m, [new("BANCO DO BRASIL", 12000m), new("FINANCEIRA BRB", 6500m)]),'),
("        Assert.Contains(\"Valor liberado para consignado neste mês: R$ 18.500,00\", model.Requests[0].Input.Replace('" + NB + "', ' '));\n",
 "        var input = model.Requests[0].Input.Replace('" + NB + "', ' ');\n"
 "        Assert.Contains(\"Total liberado para consignado neste mês: R$ 18.500,00\", input);\n"
 "        Assert.Contains(\"Reembolso: R$ 1.234,56\", input);\n"
 "        Assert.Contains(\"- Banco do Brasil: R$ 12.000,00\", input);\n"
 "        Assert.Contains(\"- Financeira Brb: R$ 6.500,00\", input);\n"
 "        Assert.Contains(model.Requests[0].CachedInstructions!, i => i.Contains(\"cada banco com o valor liberado nele, o total liberado e o reembolso\"));\n"),
("        Assert.Contains(\"Valor liberado total no SIAPE: R$ 18.500,00\", lucinea.GetProperty(\"incoming\").GetString()!.Replace('" + NB + "', ' '));\n",
 "        var incoming = lucinea.GetProperty(\"incoming\").GetString()!.Replace('" + NB + "', ' ');\n"
 "        Assert.Contains(\"Valor liberado total no SIAPE: R$ 18.500,00 · reembolso R$ 1.234,56\", incoming);\n"
 "        Assert.Contains(\"BANCO DO BRASIL: R$ 12.000,00\", incoming);\n"),
])
