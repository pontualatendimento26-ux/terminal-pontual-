using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Api;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>Falha ao falar com o SQL do SIAPE; a mensagem já está pronta para a tela.</summary>
public sealed class SiapeFailure(string message) : Exception(message);

/// <summary>Servidor, banco e login SQL da base do SIAPE, como estão gravados na Central.</summary>
public sealed record SiapeAccess(string Server, string Database, string User, string Password)
{
    public string ConnectionString => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = Database,
        UserID = User,
        Password = Password,
        TrustServerCertificate = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        ConnectTimeout = 10,
        ApplicationName = "Central do TI",
    }.ConnectionString;
}

/// <summary>Uma tabela do banco do SIAPE, com as colunas que ela tem.</summary>
public sealed record SiapeTable(string Name, DateTime CreatedAt, IReadOnlyList<string> Columns)
{
    public bool Has(string column) => Columns.Contains(column, StringComparer.OrdinalIgnoreCase);
    /// <summary>Serve como base do mês: tem CPF, nome, telefone, valor liberado total e positivação.</summary>
    public bool HasPhone => Has("cpf") && Has("nome") && Has("TELEFONE1") && Has("TOTAL_LIBERADO") && Has("POSITIVACAO");
    /// <summary>Ano e mês lidos do nome ("COMPLETA_NOVEMBRO_CORRIGIDA", "SERV 2026 11 MARGEM"), ou nulo.</summary>
    public int? MonthKey => SiapeMargin.MonthOf(Name, CreatedAt);
}

/// <summary>Um contrato do servidor na base: o banco e o valor liberado nele (colunas <c>banco</c> e <c>VALOR_LIBERADO</c>).</summary>
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

/// <summary>Lê a base do SIAPE. É uma interface para os testes trocarem o SQL Server por uma lista.</summary>
public interface ISiapeSource
{
    /// <summary>Entra no servidor e lista as tabelas do banco com as colunas de cada uma.</summary>
    Task<(string Server, IReadOnlyList<SiapeTable> Tables)> ProbeAsync(SiapeAccess access, CancellationToken ct);

    /// <summary>
    /// Os servidores da tabela do mês com positivação "NÃO" e valor liberado total (TOTAL_LIBERADO) de pelo menos
    /// <paramref name="minLiberado"/>, um por CPF, do maior valor para o menor.
    /// </summary>
    Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, decimal minLiberado, int take, CancellationToken ct);
}

/// <summary>A base do SIAPE no SQL Server (instância na máquina da Kamila).</summary>
public sealed class SqlSiapeSource : ISiapeSource
{
    public async Task<(string Server, IReadOnlyList<SiapeTable> Tables)> ProbeAsync(SiapeAccess access, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(access.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT CONVERT(nvarchar(128), @@SERVERNAME);
                SELECT t.name, t.create_date, c.name
                FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id
                ORDER BY t.create_date DESC, t.name, c.column_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            var server = await reader.ReadAsync(ct) ? reader.GetString(0) : "";
            await reader.NextResultAsync(ct);
            var tables = new List<(string Name, DateTime CreatedAt, List<string> Columns)>();
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (tables.Count == 0 || tables[^1].Name != name)
                {
                    tables.Add((name, DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Unspecified), []));
                }

                tables[^1].Columns.Add(reader.GetString(2));
            }

            return (server, tables.Select(t => new SiapeTable(t.Name, t.CreatedAt, t.Columns)).ToList());
        }
        catch (SqlException ex)
        {
            throw new SiapeFailure(Translate(ex));
        }
        catch (InvalidOperationException ex)
        {
            throw new SiapeFailure($"Não deu para falar com o SQL do SIAPE: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, decimal minLiberado, int take, CancellationToken ct)
    {
        // O nome vem da lista do próprio servidor (sys.tables), nunca de quem usa a tela; mesmo assim só entra entre colchetes.
        static string Quoted(string name) => "[" + name.Replace("]", "]]") + "]";
        string Col(string column, string fallback) => current.Has(column) ? Quoted(column) : fallback;
        // Uma linha por contrato na base (banco + VALOR_LIBERADO); TOTAL_LIBERADO e REEMBOLSO repetem em todas as linhas do CPF.
        // Primeiro escolhe os CPFs (só quem está com "NÃO" na positivação e liberado acima do mínimo), depois traz os contratos deles.
        var sql = $"""
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
            """;
        try
        {
            await using var connection = new SqlConnection(access.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 900;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 5000));
            command.Parameters.AddWithValue("@min", minLiberado);
            var list = new List<SiapeCandidate>();
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

            // O mesmo banco costuma ter vários contratos do cliente: na mensagem vai um valor por banco (a soma bate com o TOTAL_LIBERADO).
            return list.Select(c => c with
            {
                Contracts = c.Contracts.GroupBy(x => x.Bank, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new SiapeContract(g.Key, g.Sum(x => x.Liberado))).OrderByDescending(x => x.Liberado).ToList(),
            }).ToList();
        }
        catch (SqlException ex)
        {
            throw new SiapeFailure(Translate(ex));
        }
    }

    private static string Translate(SqlException ex) => ex.Number switch
    {
        18456 => "O SQL do SIAPE recusou o usuário ou a senha.",
        4060 => "O SQL do SIAPE não achou o banco informado (ou o usuário não tem acesso a ele).",
        -2 => "O SQL do SIAPE demorou demais para responder.",
        _ when ex.Message.Contains("error: 26", StringComparison.Ordinal) || ex.Message.Contains("error: 40", StringComparison.Ordinal) || ex.Number is 53 or 10061 or 11001
            => "O SQL do SIAPE não respondeu. Confira o endereço (use IP,porta: 192.168.0.160,1433) e se o computador da Kamila está ligado.",
        _ => $"O SQL do SIAPE respondeu com erro: {ex.Message}",
    };
}

/// <summary>
/// Valor liberado do SIAPE (pedido do dono, 26/11/2026): todo começo de mês a Central olha na base do SIAPE quem tem
/// valor liberado total (TOTAL_LIBERADO) acima de R$ 1.000 e ainda está com "NÃO" na positivação; a IA escreve a mensagem
/// para cada um, como a atendente, e ela sai pelo CRM. Por padrão espera aprovação em Mensagens para aprovar.
/// </summary>
public static class SiapeMargin
{
    /// <summary>Marca das mensagens em <see cref="CrmReply.Kind"/>.</summary>
    public const string Kind = "margem";
    /// <summary>Um telefone não recebe outra oferta antes disso.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromDays(150);
    public const int DefaultMax = 50;
    public const int MaxPerRun = 500;
    public const decimal DefaultMinLiberado = 1000m;

    public sealed record Result(int Checked, int Created, int Sent, string Note);

    public sealed record Settings(bool Enabled, bool AutoSend, int Day, int Max, decimal MinLiberado, string Table, string BotId, string Attendant)
    {
        public static Settings From(Dictionary<string, string> values)
        {
            static int Int(string text, int fallback, int min, int max) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : fallback;
            static decimal Money(string text, decimal fallback) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : fallback;
            return new Settings(
                values.GetValueOrDefault(TextSetting.SiapeEnabled, "") == "1",
                values.GetValueOrDefault(TextSetting.SiapeAutoSend, "") == "1",
                Int(values.GetValueOrDefault(TextSetting.SiapeDay, ""), 1, 1, 28),
                Int(values.GetValueOrDefault(TextSetting.SiapeMax, ""), DefaultMax, 1, MaxPerRun),
                Money(values.GetValueOrDefault(TextSetting.SiapeMinLiberado, ""), DefaultMinLiberado),
                values.GetValueOrDefault(TextSetting.SiapeTable, ""),
                values.GetValueOrDefault(TextSetting.SiapeBotId, ""),
                values.GetValueOrDefault(TextSetting.SiapeAttendant, ""));
        }
    }

    /// <summary>O acesso gravado, ou nulo se falta servidor, banco, login ou senha.</summary>
    public static async Task<SiapeAccess?> AccessAsync(PontualDb db, CancellationToken ct)
    {
        var values = await db.TextSettings.AsNoTracking().Where(s => s.Key.StartsWith("siape.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return Access(values);
    }

    public static SiapeAccess? Access(Dictionary<string, string> values)
    {
        var server = values.GetValueOrDefault(TextSetting.SiapeServer, "").Trim();
        var database = values.GetValueOrDefault(TextSetting.SiapeDatabase, "").Trim();
        var user = values.GetValueOrDefault(TextSetting.SiapeUser, "").Trim();
        var password = AssistantSecrets.Unprotect(values.GetValueOrDefault(TextSetting.SiapePassword, "")) ?? "";
        return server.Length > 0 && database.Length > 0 && user.Length > 0 && password.Length > 0 ? new SiapeAccess(server, database, user, password) : null;
    }

    // ------------------------------------------------------------------ tabelas do mês

    private static readonly string[] Months = ["janeiro", "fevereiro", "marco", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"];

    /// <summary>
    /// Ano e mês (ano*12+mês-1) lidos do nome da tabela: "COMPLETA_NOVEMBRO_CORRIGIDA" (o ano vem da data de criação),
    /// "SERV 2026 11 MARGEM", "MARGENS_2026_10". Nulo quando o nome não diz o mês.
    /// </summary>
    public static int? MonthOf(string name, DateTime createdAt)
    {
        var folded = CrmAgent.Fold(name);
        var numeric = Regex.Match(folded, @"(?<!\d)(20\d{2})[ _\-]?(0?[1-9]|1[0-2])(?!\d)");
        if (numeric.Success)
        {
            return int.Parse(numeric.Groups[1].Value, CultureInfo.InvariantCulture) * 12 + int.Parse(numeric.Groups[2].Value, CultureInfo.InvariantCulture) - 1;
        }

        for (var i = 0; i < Months.Length; i++)
        {
            if (!Regex.IsMatch(folded, $@"(?<![a-z]){Months[i]}(?![a-z])"))
            {
                continue;
            }

            var year = Regex.Match(folded, @"(?<!\d)(20\d{2})(?!\d)") is { Success: true } y ? int.Parse(y.Groups[1].Value, CultureInfo.InvariantCulture) : createdAt.Year;
            // Tabela de dezembro criada em janeiro é do ano anterior.
            if (!Regex.IsMatch(folded, @"(?<!\d)20\d{2}(?!\d)") && i + 1 > createdAt.Month + 1)
            {
                year--;
            }

            return year * 12 + i;
        }

        return null;
    }

    public static string MonthName(int key) => $"{CultureInfo.GetCultureInfo("pt-BR").DateTimeFormat.GetMonthName(key % 12 + 1)} de {key / 12}";

    /// <summary>
    /// A tabela do mês. Com nome escolhido na tela, vale ele; "automático" pega a base mais nova (pelo mês no nome) que tem
    /// telefone, valor liberado e positivação. Devolve também a explicação para a tela.
    /// </summary>
    public static (SiapeTable? Current, string Why) Choose(IReadOnlyList<SiapeTable> tables, string tableSetting)
    {
        var current = tableSetting.Length == 0 ? null : tables.FirstOrDefault(t => string.Equals(t.Name, tableSetting, StringComparison.OrdinalIgnoreCase));
        if (tableSetting.Length > 0 && current is null)
        {
            return (null, $"A tabela escolhida \"{tableSetting}\" não existe mais no banco. Escolha outra ou volte para automático.");
        }

        current ??= tables.Where(t => t.HasPhone).OrderByDescending(t => t.MonthKey ?? int.MinValue).ThenByDescending(t => t.CreatedAt).FirstOrDefault();
        if (current is null)
        {
            return (null, "Nenhuma tabela do banco tem as colunas cpf, nome, TELEFONE1, TOTAL_LIBERADO e POSITIVACAO.");
        }

        if (!current.HasPhone)
        {
            return (null, $"A tabela \"{current.Name}\" não tem as colunas cpf, nome, TELEFONE1, TOTAL_LIBERADO e POSITIVACAO.");
        }

        return (current, $"Base do mês: {current.Name}{(current.MonthKey is { } k ? $" ({MonthName(k)})" : "")}. Entra quem está com \"NÃO\" na positivação e tem valor liberado total acima do mínimo.");
    }

    // ------------------------------------------------------------------ telefone e nome

    /// <summary>O celular do SIAPE no formato do CRM ("5524999999967@c.us"), ou vazio se não é um celular brasileiro.</summary>
    public static string ChatIdOf(string phone)
    {
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 13 && digits.StartsWith("55", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        return digits.Length == 11 && digits[0] != '0' && digits[2] == '9' ? $"55{digits}@c.us" : "";
    }

    /// <summary>"MARIA DA SILVA" vira "Maria da Silva".</summary>
    public static string Pretty(string name)
    {
        var words = name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((w, i) => i > 0 && w is "da" or "de" or "do" or "das" or "dos" or "e" ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public static string Money(decimal value) => value.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));

    // ------------------------------------------------------------------ IA

    public const string Rules =
        """
        MODO VALOR LIBERADO (SIAPE)

        Agora você escreve a PRIMEIRA mensagem para um servidor público federal (SIAPE) que ainda não conversou com a Pontual neste atendimento. A base do mês mostra que ele tem um valor liberado para crédito consignado: é isso que você vai contar. Os dados dele estão em <cliente>.

        - Escreva como a atendente indicada em <atendente> (ou no jeito geral da equipe), em primeira pessoa, no WhatsApp: curto (até umas cinco linhas), sem título e sem markdown; para destacar use *asteriscos*.
        - Comece se apresentando pela Pontual (correspondente bancário de crédito consignado) e chame o cliente pelo primeiro nome.
        - O assunto é o valor liberado: diga que neste mês ele está com um valor liberado para consignado no contracheque e que dá para aproveitar (empréstimo novo, portabilidade com troco ou refinanciamento, conforme a base).
        - A mensagem TEM QUE trazer, exatamente como estão em <cliente>: o nome do cliente, cada banco com o valor liberado nele, o total liberado e o reembolso. Copie os valores como estão (R$ com centavos), um banco por linha quando houver mais de um. Fora isso, nunca invente valor de parcela, taxa ou prazo.
        - Termine com um convite simples e uma pergunta só: se ele quer que você faça uma simulação sem compromisso. Não peça documento nem CPF nesta primeira mensagem.
        - Respeito e sem pressa: nada de urgência falsa, promessa, "última chance" ou pressão. Se o cliente for idoso (idade em <cliente>), trate por senhor/senhora.
        - Nunca diga que vai passar para outra pessoa nem fale da atendente em terceira pessoa.
        - Use "responder" com a mensagem em "mensagem". Use "nada" só se os dados em <cliente> não fizerem sentido (sem nome, valor zerado). Em "motivo", uma frase para quem vai aprovar.
        """;

    /// <summary>Procura os clientes com margem nova e escreve as mensagens. <paramref name="force"/> roda mesmo com a rotina desligada (botão da tela).</summary>
    public static async Task<Result> ScanAsync(IServiceProvider services, bool force, CancellationToken ct)
    {
        var db = services.GetRequiredService<PontualDb>();
        var source = services.GetRequiredService<ISiapeSource>();
        var crm = services.GetRequiredService<CrmClient>();
        var model = services.GetRequiredService<IAssistantModel>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var values = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var settings = Settings.From(values);
        if (!force && !settings.Enabled)
        {
            return new Result(0, 0, 0, "A busca mensal de valor liberado está desligada.");
        }

        if (Access(values) is not { } access)
        {
            return new Result(0, 0, 0, "A ligação com o SQL do SIAPE não está configurada.");
        }

        if (await CrmClient.AccessAsync(db, ct) is not { } crmAccess)
        {
            return new Result(0, 0, 0, "A ligação com o CRM não está configurada: a mensagem sai por ele.");
        }

        if (settings.BotId.Length == 0)
        {
            return new Result(0, 0, 0, "Escolha a linha de WhatsApp do CRM por onde as mensagens vão sair.");
        }

        if (await AssistantSecrets.ReadKeyAsync(db, configuration, ct) is not { } apiKey)
        {
            return new Result(0, 0, 0, "A IA está sem chave: cadastre em Central do TI → Assistente.");
        }

        var (_, tables) = await source.ProbeAsync(access, ct);
        var (current, why) = Choose(tables, settings.Table);
        if (current is null)
        {
            return new Result(0, 0, 0, why);
        }

        // Quem já recebeu oferta nos últimos meses (em qualquer situação) não recebe outra.
        var since = now - Quiet;
        var recent = await db.CrmReplies.AsNoTracking().Where(r => r.Kind == Kind && r.CreatedAt > since).Select(r => r.ChatId).ToListAsync(ct);
        var excluded = recent.ToHashSet(StringComparer.Ordinal);
        var candidates = await source.CandidatesAsync(access, current, settings.MinLiberado, settings.Max + excluded.Count + 20, ct);

        var (cached, _) = await CrmEndpoints.InstructionsAsync(db, values, "", ct);
        var learned = await CrmEndpoints.LearnedAsync(db, settings.Attendant, ct);
        var created = 0;
        var sent = 0;
        var checkedCount = 0;
        var failures = 0;
        foreach (var candidate in candidates)
        {
            if (created >= settings.Max)
            {
                break;
            }

            var chatId = ChatIdOf(candidate.Phone);
            if (chatId.Length == 0 || excluded.Contains(chatId) || candidate.Name.Length == 0)
            {
                continue;
            }

            checkedCount++;
            excluded.Add(chatId);
            var reply = Draft(candidate, chatId, settings, current, now);
            try
            {
                var request = new AssistantJsonRequest(apiKey, AssistantEndpoints.ModelOf(values), "", PromptFor(candidate, settings.Attendant) + learned,
                    CrmAgent.Schema, [.. cached, Rules], new AssistantUsageTag(UsageKind.Crm));
                var decision = await CrmAgent.WithoutHandoffAsync(model, request, CrmAgent.ParseDecision(await model.AskJsonAsync(request, ct)), settings.Attendant, ct);
                reply.Decision = decision.Action;
                reply.Reply = decision.Message;
                reply.Draft = decision.Message;
                reply.Reason = Input.Truncated(decision.Reason, 400);
                if (decision.Action == CrmAgent.None || decision.Message.Length == 0)
                {
                    reply.Status = CrmReplyStatus.Skipped;
                }
            }
            catch (AssistantFailure failure)
            {
                reply.Status = CrmReplyStatus.Failed;
                reply.Error = $"{failure.Message} Você pode escrever a mensagem à mão e enviar.";
                failures++;
            }

            if (reply.Status == CrmReplyStatus.Pending)
            {
                created++;
                if (settings.AutoSend)
                {
                    await SendAsync(crm, crmAccess, reply, now, ct);
                    if (reply.Status == CrmReplyStatus.Sent)
                    {
                        sent++;
                    }
                }
            }

            db.CrmReplies.Add(reply);
            await db.SaveChangesAsync(ct);
        }

        var note = created == 0
            ? checkedCount == 0 ? "Nenhum servidor com positivação \"NÃO\" e valor liberado acima do mínimo (que ainda não recebeu oferta) na base do mês." : "A IA não escreveu mensagem para nenhum dos servidores encontrados."
            : settings.AutoSend ? $"{sent} de {created} mensagem(ns) enviada(s) pelo CRM." : $"{created} mensagem(ns) de valor liberado esperando aprovação.";
        if (failures > 0)
        {
            note += $" {failures} falhou(aram) na IA.";
        }

        return new Result(checkedCount, created, sent, $"{why} {note}");
    }

    private static CrmReply Draft(SiapeCandidate c, string chatId, Settings settings, SiapeTable current, DateTime now)
    {
        var facts = new StringBuilder()
            .Append("Valor liberado total no SIAPE: ").Append(Money(c.Liberado))
            .Append(c.Refund != 0 ? $" · reembolso {Money(c.Refund)}" : "")
            .Append(c.Margin != 0 ? $" (margem do mês {Money(c.Margin)})" : "").Append(" · positivação: NÃO\n");
        foreach (var contract in c.Contracts)
        {
            facts.Append(contract.Bank.Length > 0 ? contract.Bank : "Banco não informado").Append(": ").Append(Money(contract.Liberado)).Append('\n');
        }

        facts.Append("CPF ").Append(NexusClient.FormatCpf(c.Cpf));
        if (c.Organ.Length > 0)
        {
            facts.Append(" · ").Append(c.Organ);
        }

        if (c.Uf.Length > 0)
        {
            facts.Append(" · ").Append(c.Uf);
        }

        if (c.Age > 0)
        {
            facts.Append(" · ").Append(c.Age).Append(" anos");
        }

        facts.Append("\nBase: ").Append(current.Name);
        var digits = chatId.Split('@')[0];
        return new CrmReply
        {
            Id = Guid.NewGuid(), ConversationId = $"{chatId}|{settings.BotId}", Kind = Kind, ChatId = chatId, BotId = settings.BotId,
            Contact = Pretty(c.Name), Phone = digits, Incoming = facts.ToString(), Transcript = "", Attendant = settings.Attendant,
            CreatedAt = now, UpdatedAt = now, Status = CrmReplyStatus.Pending,
        };
    }

    private static string PromptFor(SiapeCandidate c, string attendant)
    {
        var text = new StringBuilder()
            .Append("<atendente>").Append(attendant.Length > 0 ? attendant : "não definida: escreva no jeito geral da equipe").Append("</atendente>\n")
            .Append("<cliente>\n")
            .Append("Nome: ").Append(Pretty(c.Name)).Append('\n')
            .Append("Convênio: SIAPE (servidor público federal)").Append(c.Organ.Length > 0 ? $", órgão {c.Organ}" : "").Append(c.Uf.Length > 0 ? $", {c.Uf}" : "").Append('\n');
        if (c.Age > 0)
        {
            text.Append("Idade: ").Append(c.Age).Append(" anos\n");
        }

        text.Append("Total liberado para consignado neste mês: ").Append(Money(c.Liberado)).Append('\n');
        text.Append("Reembolso: ").Append(Money(c.Refund)).Append('\n');
        if (c.Contracts.Count > 0)
        {
            text.Append("Valor liberado por banco:\n");
            foreach (var contract in c.Contracts)
            {
                text.Append("- ").Append(contract.Bank.Length > 0 ? Pretty(contract.Bank) : "Banco não informado").Append(": ").Append(Money(contract.Liberado)).Append('\n');
            }
        }

        if (c.Margin != 0)
        {
            text.Append("Margem consignável do mês: ").Append(Money(c.Margin)).Append('\n');
        }

        return text.Append("Ainda não foi positivado (ninguém da Pontual ofereceu este valor a ele).\n</cliente>").ToString();
    }

    /// <summary>Manda a mensagem ao cliente pela tela de conversa do CRM, na linha escolhida, e marca a etiqueta da IA.</summary>
    public static async Task SendAsync(CrmClient crm, CrmAccess access, CrmReply reply, DateTime now, CancellationToken ct)
    {
        try
        {
            await crm.SendChatAsync(access, reply.ChatId, reply.BotId, reply.Reply, ct);
        }
        catch (CrmFailure failure)
        {
            reply.Status = CrmReplyStatus.Failed;
            reply.Error = failure.Message;
            reply.UpdatedAt = now;
            return;
        }

        reply.Status = CrmReplyStatus.Sent;
        reply.ReviewedByName = "IA (envio automático)";
        reply.SentAt = now;
        reply.UpdatedAt = now;
        try
        {
            await crm.TagAsync(access, reply.ChatId, reply.BotId, ct);
        }
        catch (CrmFailure failure)
        {
            reply.Error = $"Enviada, mas sem a etiqueta \"{CrmAgent.AiTag}\": {failure.Message}";
        }
    }
}

/// <summary>
/// Roda a busca de valor liberado uma vez por mês, a partir do dia escolhido (padrão dia 1), das 8h às 18h de
/// Brasília, de segunda a sábado, se a rotina estiver ligada. Confere a cada hora se já é a vez.
/// </summary>
public sealed class SiapeMarginWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<SiapeMarginWorker> logger) : BackgroundService
{
    private static readonly SemaphoreSlim Running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await DueAsync(stoppingToken))
                    {
                        await RunAsync(scopes, time, logger, force: false, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Falha ao conferir a busca mensal de valor liberado do SIAPE");
                }

                await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Ligada, no dia (ou depois dele), em horário de trabalho, e ainda não rodou neste mês.</summary>
    private async Task<bool> DueAsync(CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, AssistantPrompt.BrazilTime);
        if (local.DayOfWeek == DayOfWeek.Sunday || local.Hour is < 8 or >= 18)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
        var values = await db.TextSettings.AsNoTracking().Where(s => s.Key.StartsWith("siape.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var settings = SiapeMargin.Settings.From(values);
        return settings.Enabled && local.Day >= settings.Day && values.GetValueOrDefault(TextSetting.SiapeLastMonth, "") != local.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    /// <summary>Uma busca, uma de cada vez; grava quando rodou e o que achou para a tela.</summary>
    public static async Task<SiapeMargin.Result> RunAsync(IServiceScopeFactory scopes, TimeProvider time, ILogger logger, bool force, CancellationToken ct)
    {
        if (!await Running.WaitAsync(0, ct))
        {
            return new SiapeMargin.Result(0, 0, 0, "Já há uma busca de margem rodando; espere ela terminar.");
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            SiapeMargin.Result result;
            try
            {
                result = await SiapeMargin.ScanAsync(scope.ServiceProvider, force, ct);
            }
            catch (SiapeFailure failure)
            {
                result = new SiapeMargin.Result(0, 0, 0, $"A busca parou: {failure.Message}");
            }
            catch (CrmFailure failure)
            {
                result = new SiapeMargin.Result(0, 0, 0, $"A busca parou no CRM: {failure.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha na busca de valor liberado do SIAPE");
                result = new SiapeMargin.Result(0, 0, 0, "A busca falhou por um erro interno; está no registro da Central.");
            }

            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            var now = time.GetUtcNow().UtcDateTime;
            await SetAsync(db, TextSetting.SiapeLastRun, $"{now:O}|{result.Checked}|{result.Created}|{result.Note}", ct);
            // Só a rodada que leu a base conta como "o mês já foi": uma falha de ligação tenta de novo na próxima hora.
            if (!force && (result.Checked > 0 || result.Note.StartsWith("Base do mês", StringComparison.Ordinal)))
            {
                await SetAsync(db, TextSetting.SiapeLastMonth, TimeZoneInfo.ConvertTimeFromUtc(now, AssistantPrompt.BrazilTime).ToString("yyyy-MM", CultureInfo.InvariantCulture), ct);
            }

            return result;
        }
        finally
        {
            Running.Release();
        }
    }

    private static async Task SetAsync(PontualDb db, string key, string value, CancellationToken ct)
    {
        var row = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.TextSettings.Add(new TextSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }
}
