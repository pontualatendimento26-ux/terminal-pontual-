using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Api;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>
/// Clientes perdidos do CRM: no funil Atendimento Portabilidade, conversas em que o cliente escreveu por último e
/// ninguém respondeu há mais de um dia (olhando até 15 dias para trás). A IA lê cada uma, decide se o cliente
/// espera mesmo resposta e escreve no jeito da atendente dona do cliente (o "Responsável" no CRM). A resposta
/// espera aprovação em Mensagens para aprovar e sai pela tela de conversa do CRM, na linha do cliente.
/// </summary>
public static class CrmLost
{
    public const string Kind = "perdido";
    /// <summary>O funil Atendimento Portabilidade.</summary>
    public const string Funnel = "4";
    /// <summary>Sem resposta há menos que isso ainda não é perdido: a atendente pode só estar ocupada.</summary>
    public static readonly TimeSpan MinWait = TimeSpan.FromHours(24);
    /// <summary>Até onde a busca olha para trás.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(15);
    /// <summary>No máximo tantas conversas lidas pela IA por busca: segura o gasto.</summary>
    public const int MaxCalls = 25;
    private const int MaxPages = 40;
    /// <summary>Conversas resolvidas sem a IA (já finalizadas) por busca: não gastam, mas a busca não pode durar para sempre.</summary>
    private const int MaxFree = 200;
    /// <summary>Começo do motivo de quem ficou de fora por já estar finalizado.</summary>
    public const string FinishedReason = "Finalizado";
    private const int TranscriptMessages = 40;

    private static readonly HashSet<string> SystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "notification_template", "notification", "e2e_notification", "gp2", "call_log", "ciphertext", "revoked", "comentario", "lembrete", "protocol",
    };

    public sealed record Result(int Checked, int Created, string Note);

    /// <summary>Uma fala do histórico do CRM.</summary>
    public sealed record Message(DateTime At, bool FromMe, string Who, string Text, string WppId);

    public const string Rules =
        """
        MODO CLIENTE SEM RESPOSTA

        Este cliente mandou mensagem e ficou sem resposta da Pontual há bastante tempo (veja as datas na conversa). Você vai retomar a conversa como a atendente dona do cliente, indicada em <atendente>: pense e escreva como ela.

        - Escreva no jeito dela: veja como ela escreve nas falas marcadas com o nome dela na conversa e nos exemplos aprovados (tamanho, cumprimento, emojis, forma de tratar o cliente). Não assine e não diga o nome dela se ela não costuma dizer.
        - Para o cliente, quem escreve é ela mesma: sempre em primeira pessoa, nunca "vou chamar a Kamila", "a Kamila vai te responder", "vou passar para a atendente" ou "vou confirmar com ela". O nome da atendente em <atendente> serve para você saber de quem é o jeito de escrever, seja ela quem for. Quando algo depende de outra pessoa (áudio, ligação, consulta), não diga quem vai fazer: "vou verificar e te retorno por aqui".
        - Comece retomando com naturalidade e um pedido de desculpas curto pela demora, e responda de verdade o que o cliente perguntou ou conduza para o próximo passo (por exemplo, pedir o CPF ou o contracheque para simular).
        - "nada" quando o cliente não espera resposta: agradeceu, mandou "ok", figurinha, encerrou, disse que não quer mais, pediu para não ser chamado, ou a mensagem é automática.
        - Cliente FINALIZADO é sempre "nada", e o "motivo" começa com "Finalizado:". É o atendimento que já chegou ao fim: a Pontual já disse que não dá (sem possibilidade, margem negativa, reprovado, sem banco, idade, prazo), a proposta já foi concluída ou paga, ou o cliente desistiu, e o que ele escreveu depois só fecha a conversa ("ok", "tá", "viu", "obrigado", "vou ver", "deixa pra próxima"). Não reabra esse atendimento, não ofereça nova simulação e não use "humano" só para a atendente rever o caso.
        - <situacao_no_crm> diz em que etapa o cliente está. Em "Ganho" ou "Perdido" a Pontual já encerrou o caso: só responda se o cliente pediu ou perguntou algo novo que ficou sem resposta.
        - Se a Pontual disse que não dá e o cliente perguntou o porquê, discordou ou mostrou que não entendeu, ele NÃO está finalizado: a pergunta espera resposta.
        - Em <ja_finalizados> estão conversas que a equipe marcou como finalizadas: trate como elas as que forem parecidas.
        - "humano" quando só a atendente pode responder (valor, proposta ou situação de contrato que não está na conversa, reclamação, cancelamento). Em "mensagem" escreva, como ela, um texto curto que retoma e assume o retorno ("Desculpa a demora! Vou verificar a sua situação e te dou um retorno por aqui"), reconhecendo o que o cliente contou; em "motivo" diga o que a atendente precisa fazer para cumprir esse retorno.
        """;

    /// <summary>Procura os clientes perdidos e escreve as respostas. <paramref name="force"/> roda mesmo com a busca desligada (botão da tela).</summary>
    /// <summary>Até quantos dias para trás a tela deixa escolher.</summary>
    public const int MaxDays = 60;
    /// <summary>Com datas escolhidas na tela, quem escreveu há menos que isso ainda fica de fora: a atendente pode estar respondendo.</summary>
    public static readonly TimeSpan MinWaitChosen = TimeSpan.FromHours(1);

    /// <summary>
    /// Procura os clientes perdidos e escreve as respostas. <paramref name="force"/> roda mesmo com a busca desligada (botão da tela).
    /// Sem datas: de 15 dias atrás até 24 horas atrás. Com datas (dias de Brasília, inclusive): o período escolhido, até 1 hora atrás.
    /// </summary>
    public static async Task<Result> ScanAsync(IServiceProvider services, bool force, CancellationToken ct, DateTime? fromDay = null, DateTime? toDay = null)
    {
        var db = services.GetRequiredService<PontualDb>();
        var crm = services.GetRequiredService<CrmClient>();
        var model = services.GetRequiredService<IAssistantModel>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        if (!force && settings.GetValueOrDefault(TextSetting.CrmLostEnabled, "") != "1")
        {
            return new Result(0, 0, "A busca dos clientes perdidos está desligada.");
        }

        if (await CrmClient.AccessAsync(db, ct) is not { } access)
        {
            return new Result(0, 0, "A ligação com o CRM não está configurada.");
        }

        if (await AssistantSecrets.ReadKeyAsync(db, configuration, ct) is not { } apiKey)
        {
            return new Result(0, 0, "A IA está sem chave: cadastre em Central do TI → Assistente.");
        }

        var lines = (await crm.LinesAsync(access, ct)).Where(l => l.Funnel == Funnel).Select(l => l.Id).ToHashSet();
        if (lines.Count == 0)
        {
            return new Result(0, 0, "Nenhuma linha de WhatsApp no funil Atendimento Portabilidade.");
        }

        var users = (await crm.UsersAsync(access, ct)).ToDictionary(u => u.Id, u => u.Name);
        var today = TimeZoneInfo.ConvertTimeFromUtc(now, AssistantPrompt.BrazilTime).Date;
        // A busca automática também obedece ao período escolhido na tela, quando há um.
        if (!force && fromDay is null && toDay is null && ReadPeriod(settings.GetValueOrDefault(TextSetting.CrmLostPeriod, "")) is { } saved)
        {
            (fromDay, toDay) = saved;
        }

        var chosen = fromDay is not null || toDay is not null;
        var firstDay = (fromDay ?? today - Window).Date;
        var lastDay = (toDay ?? today).Date;
        var (periodStart, periodEnd) = Bounds(firstDay, lastDay);
        var oldest = chosen ? periodStart : now - Window;
        // Só quem clicou em Buscar pega quem espera há 1 hora; sozinha, a busca continua esperando um dia.
        var wait = now - (force && chosen ? MinWaitChosen : MinWait);
        var newest = chosen && periodEnd < wait ? periodEnd : wait;

        // Antes de procurar, tira da fila o que não vale mais (já respondido, repetido, finalizado). Não usa a IA.
        var finished = await RefreshAsync(db, crm, access, now, ct);

        // Um cliente é um cartão só, em qualquer linha. Já tratados: a mesma última mensagem não é lida de novo.
        var seen = await db.CrmReplies.AsNoTracking().Where(r => r.Kind == Kind)
            .Select(r => new { r.Id, r.ChatId, r.BotId, r.LastWppId, r.Status, r.WaitingSince, r.SentAt }).ToListAsync(ct);
        var done = seen.Select(r => $"{r.ChatId}|{r.BotId}|{r.LastWppId}").ToHashSet();
        var handled = seen.Where(r => r.Status is not (CrmReplyStatus.Outdated or CrmReplyStatus.Pending or CrmReplyStatus.Failed)).ToLookup(r => r.ChatId);
        var open = seen.Where(r => r.Status is CrmReplyStatus.Pending or CrmReplyStatus.Failed)
            .GroupBy(r => r.ChatId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.WaitingSince).Select(r => (r.Id, r.BotId, r.WaitingSince)).First());

        var (cached, _) = (await CrmEndpoints.InstructionsAsync(db, settings, "", ct));
        var calls = 0;
        var free = 0;
        var created = 0;
        var checkedCount = 0;
        for (var page = 1; page <= MaxPages && calls < MaxCalls && free < MaxFree; page++)
        {
            var (chats, more) = await crm.WaitingChatsAsync(access, lines, firstDay, lastDay, page, ct);
            if (chats.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var reachedOld = false;
            foreach (var chat in chats.EnumerateArray())
            {
                if (calls >= MaxCalls || free >= MaxFree)
                {
                    break;
                }

                var chatId = Str(chat, "chat_id");
                var lastAt = Time(Str(chat, "last_message_at"), utc: true);
                if (lastAt is { } stale && stale < oldest)
                {
                    reachedOld = true;
                    break;
                }

                // Com período escolhido, quem teve conversa depois dele (mesmo em outra linha) não é cliente daquela data.
                if (chosen && lastAt >= periodEnd)
                {
                    continue;
                }

                if (!chat.TryGetProperty("canais", out var channels) || channels.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // Só a conversa mais recente do cliente, entre todas as linhas dele (o RCS de divulgação não conta): se em outra
                // linha a Pontual já respondeu, ou se ele seguiu por outro funil, não é cliente perdido.
                var latest = channels.EnumerateArray()
                    .Select(c => (Channel: c, At: Time(Str(c, "ts"), utc: false), Bot: Str(c, "bot_id")))
                    .Where(c => c.At is not null && !c.Bot.StartsWith("rcs", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.At).FirstOrDefault();
                var (channel, at, botId) = latest;
                if (at is null || chatId.Length == 0 || !lines.Contains(botId) || at >= newest || at < oldest
                    || channel.TryGetProperty("from_me", out var fromMe) && fromMe.ValueKind != JsonValueKind.False)
                {
                    continue;
                }

                checkedCount++;
                var wpp = channel.TryGetProperty("ultima", out var last) ? Str(last, "wpp") : "";
                var waitingOpen = open.TryGetValue(chatId, out var current);
                if (done.Contains($"{chatId}|{botId}|{wpp}")
                    || waitingOpen && (current.BotId == botId || current.WaitingSince >= at)
                    || handled[chatId].Any(r => r.WaitingSince > at || r.SentAt > at))
                {
                    continue;
                }

                var owner = users.GetValueOrDefault(Str(channel, "responsavel_id"), "");
                var (reply, asked) = await WriteAsync(db, crm, model, access, apiKey, settings, cached, chatId, botId, Str(chat, "name"), owner, now, oldest, newest, done, ct);
                _ = asked ? calls++ : free++;
                if (reply is null)
                {
                    continue;
                }

                db.CrmReplies.Add(reply);
                await db.SaveChangesAsync(ct);
                done.Add($"{chatId}|{botId}|{reply.LastWppId}");
                if (waitingOpen)
                {
                    await CloseAsync(db, current.Id, CrmReplyStatus.Outdated, RepeatedReason, now, ct);
                    open.Remove(chatId);
                }

                if (reply.Status == CrmReplyStatus.Pending)
                {
                    created++;
                    open[chatId] = (reply.Id, botId, reply.WaitingSince);
                }
                else if (reply.Reason.StartsWith(FinishedReason, StringComparison.OrdinalIgnoreCase))
                {
                    finished++;
                }
            }

            if (reachedOld || !more)
            {
                break;
            }
        }

        var note = created > 0 ? $"{created} cliente(s) perdido(s) com resposta para aprovar."
            : checkedCount > 0 ? "Nenhum cliente perdido novo." : chosen
                ? $"Nenhuma conversa sem resposta entre {firstDay:dd/MM} e {lastDay:dd/MM} no funil."
                : "Nenhuma conversa sem resposta entre 1 e 15 dias no funil.";
        if (finished > 0)
        {
            note += $" {finished} já finalizado(s) ficaram de fora.";
        }

        if (chosen)
        {
            note = $"De {firstDay:dd/MM} a {lastDay:dd/MM}: {note}";
        }

        if (calls >= MaxCalls || free >= MaxFree)
        {
            note += $" Parou em {calls} conversas lidas pela IA para não gastar demais; a próxima busca continua.";
        }

        return new Result(checkedCount, created, note);
    }

    /// <summary>O período salvo ("2026-11-01|2026-11-20"), ou nulo se não há um que dê para ler.</summary>
    public static (DateTime From, DateTime To)? ReadPeriod(string text)
    {
        var parts = text.Split('|');
        static DateTime? Day(string value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
        return parts.Length == 2 && Day(parts[0]) is { } from && Day(parts[1]) is { } to ? (from, to) : null;
    }

    /// <summary>O período da última busca com datas ("2026-11-25T...|175|16|De 01/11 a 20/11: ..."), no formato salvo; vazio se não teve datas.</summary>
    internal static string PeriodOfLastRun(string lastRun)
    {
        var match = Regex.Match(lastRun, "^([0-9]{4})-([0-9]{2})-[^|]*[|][^|]*[|][^|]*[|]De ([0-9]{2})/([0-9]{2}) a ([0-9]{2})/([0-9]{2}):");
        if (!match.Success)
        {
            return "";
        }

        int N(int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
        string Day(int day, int month) => $"{(month > N(2) ? N(1) - 1 : N(1)):0000}-{month:00}-{day:00}";
        var period = $"{Day(N(3), N(4))}|{Day(N(5), N(6))}";
        return ReadPeriod(period) is null ? "" : period;
    }

    /// <summary>Do começo do primeiro dia até o começo do dia seguinte ao último (dias de Brasília), em UTC.</summary>
    public static (DateTime Start, DateTime End) Bounds(DateTime firstDay, DateTime lastDay)
    {
        static DateTime Utc(DateTime day) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified), AssistantPrompt.BrazilTime);
        return (Utc(firstDay), Utc(lastDay.AddDays(1)));
    }

    /// <summary>Escreve a resposta de uma conversa. Asked diz se a IA foi chamada: quem já está finalizado sai sem gastar.</summary>
    private static async Task<(CrmReply? Reply, bool Asked)> WriteAsync(PontualDb db, CrmClient crm, IAssistantModel model, CrmAccess access, string apiKey,
        Dictionary<string, string> settings, IReadOnlyList<string> cached, string chatId, string botId, string name, string owner, DateTime now,
        DateTime oldest, DateTime newest, HashSet<string> done, CancellationToken ct)
    {
        var history = Read(await crm.MessagesAsync(access, chatId, botId, ct));
        var lastIndex = history.FindLastIndex(m => m.Text.Length > 0);
        if (lastIndex < 0 || history[lastIndex].FromMe)
        {
            return (null, false);
        }

        // A lista do CRM traz a data da última atividade da linha (às vezes um aviso do sistema). Vale a da última fala do cliente;
        // e a que já foi tratada não é lida (nem paga) de novo.
        if (history[lastIndex].At >= newest || history[lastIndex].At < oldest || done.Contains($"{chatId}|{botId}|{history[lastIndex].WppId}"))
        {
            return (null, false);
        }

        var lastTeam = history.FindLastIndex(m => m.FromMe);
        var incoming = string.Join('\n', history.Skip(lastTeam + 1).Where(m => !m.FromMe).Select(m => m.Text));
        var lines = history.TakeLast(TranscriptMessages)
            .Select(m => new CrmAgent.Line(m.At, m.FromMe ? CrmAuthor.Team : CrmAuthor.Client, m.FromMe && m.Who.Length > 0 ? $"({m.Who}) {m.Text}" : m.Text));
        var transcript = CrmAgent.Transcript(lines);
        var waiting = history[lastIndex].At;
        var reply = new CrmReply
        {
            Id = Guid.NewGuid(), ConversationId = $"{chatId}|{botId}", Kind = Kind, ChatId = chatId, BotId = botId, LastWppId = history[lastIndex].WppId,
            WaitingSince = waiting, Contact = name, Phone = new string(chatId.Split('@')[0].Where(char.IsAsciiDigit).ToArray()),
            Incoming = incoming, Transcript = transcript, Attendant = owner, CreatedAt = now, UpdatedAt = now, Status = CrmReplyStatus.Pending,
        };

        var stage = await crm.StageAsync(access, reply.Phone, ct);
        if (Finished(history, stage) is { Length: > 0 } why)
        {
            reply.Decision = CrmAgent.None;
            reply.Status = CrmReplyStatus.Skipped;
            reply.Reason = why;
            return (reply, false);
        }

        var days = Math.Max(1, (int)(now - waiting).TotalDays);
        var input = new StringBuilder()
            .Append("<atendente>").Append(owner.Length > 0 ? owner : "não definida: escreva no jeito geral da equipe").Append("</atendente>\n")
            .Append("<cliente>").Append(name.Length > 0 ? name : "sem nome").Append(" — sem resposta há ").Append(days).Append(days == 1 ? " dia" : " dias").Append("</cliente>\n")
            .Append("<situacao_no_crm>").Append(stage.Length > 0 ? stage : "não informada").Append("</situacao_no_crm>\n\n")
            .Append("<conversa>\n").Append(transcript).Append("\n</conversa>\n\n")
            .Append("<mensagens_sem_resposta>\n").Append(incoming).Append("\n</mensagens_sem_resposta>")
            .Append(await FinishedExamplesAsync(db, ct))
            .Append(await CrmEndpoints.LearnedAsync(db, owner, ct));
        try
        {
            var request = new AssistantJsonRequest(apiKey, AssistantEndpoints.ModelOf(settings), "", input.ToString(),
                CrmAgent.Schema, [.. cached, Rules], new AssistantUsageTag(UsageKind.Crm));
            var decision = await CrmAgent.WithoutHandoffAsync(model, request, CrmAgent.ParseDecision(await model.AskJsonAsync(request, ct)), owner, ct);
            reply.Decision = decision.Action;
            reply.Reply = decision.Message;
            reply.Draft = decision.Message;
            reply.Reason = Input.Truncated(decision.Reason, 400);
            if (decision.Action == CrmAgent.None)
            {
                reply.Status = CrmReplyStatus.Skipped;
            }
        }
        catch (AssistantFailure failure)
        {
            reply.Status = CrmReplyStatus.Failed;
            reply.Error = $"{failure.Message} Você pode escrever a resposta à mão e enviar.";
        }

        return (reply, true);
    }

    // ------------------------------------------------------------------ já finalizados

    /// <summary>A etapa do CRM que encerra o caso.</summary>
    private static bool ClosedStage(string stage) => CrmAgent.Fold(stage) is "ganho" or "perdido";

    /// <summary>A Pontual fechando o caso, no texto sem acento da última fala dela.</summary>
    private static readonly Regex Closing = new(
        @"sem (nenhuma )?possibilidade|nao (ha|tem|temos|tera|teria) (mais )?(nenhuma )?possibilidade|nao (vamos|vou) conseguir|nao conseguimos|nao (e|foi|sera) possivel|nenhum banco|"
        + @"nao (foi )?aprovad|reprovad|sem margem|margem (do senhor |da senhora )?(esta |ta )?negativa|nao (fazemos|trabalhamos|operamos)|"
        + @"(atendimento|processo) (foi |esta )?(finalizado|encerrado|concluido)|(proposta|contrato|operacao) (ja )?(foi |esta )?(pag[oa]|averbad[oa]|finalizad[oa]|concluid[oa]|cancelad[oa])",
        RegexOptions.Compiled);

    /// <summary>As palavras de quem só confirma ou se despede. Cumprimento ("bom dia") fica de fora: pode ser um pedido novo.</summary>
    private static readonly HashSet<string> AckWords = new(StringComparer.Ordinal)
    {
        "ok", "okay", "okey", "okk", "okok", "blz", "beleza", "ta", "tah", "certo", "bom", "bem", "tudo", "entendi", "entendido", "obrigado", "obrigada", "obg", "obgd", "brigado",
        "brigada", "grato", "grata", "agradeco", "agradecido", "agradecida", "valeu", "vlw", "viu", "entao", "muito", "pela", "pelo", "atencao", "retorno", "ajuda", "por", "deus", "abencoe",
        "amem", "tranquilo", "tranquila", "perfeito", "combinado", "show", "joia", "que", "pena", "tchau", "abraco", "abracos", "ah", "a", "o", "e", "de", "senhor", "senhora", "moco",
        "moca", "voce", "vc", "eu", "sticker", "audio", "reaction",
    };

    /// <summary>A fala só confirma, agradece ou se despede (ou é só figurinha e emoji).</summary>
    internal static bool OnlyAck(string text)
    {
        var folded = CrmAgent.Fold(text);
        return !folded.Contains('?') && Regex.Matches(folded, "[a-z0-9]+").All(w => AckWords.Contains(w.Value));
    }

    /// <summary>
    /// Por que o cliente já está finalizado, ou vazio. Só quando as falas sem resposta dele apenas fecham a conversa E o caso
    /// acabou: pela etapa no CRM (Ganho/Perdido) ou porque a última fala da Pontual encerrou. Com pergunta nova, a IA lê.
    /// </summary>
    internal static string Finished(List<Message> history, string stage)
    {
        var lastTeam = history.FindLastIndex(m => m.FromMe);
        var waiting = history.Skip(lastTeam + 1).ToList();
        if (waiting.Count == 0 || !waiting.All(m => OnlyAck(m.Text)))
        {
            return "";
        }

        if (ClosedStage(stage))
        {
            return $"{FinishedReason}: está em {stage} no CRM e o cliente só confirmou.";
        }

        // As falas seguidas da Pontual logo antes da resposta do cliente.
        for (var i = lastTeam; i >= 0 && history[i].FromMe; i--)
        {
            var said = CrmAgent.Fold(history[i].Text);
            if (said.TrimEnd().EndsWith('?'))
            {
                return "";
            }

            if (Closing.IsMatch(said))
            {
                return $"{FinishedReason}: a Pontual encerrou o caso (\"{Input.Truncated(history[i].Text.ReplaceLineEndings(" "), 120)}\") e o cliente só confirmou.";
            }
        }

        return "";
    }

    /// <summary>O aviso de quem saiu da fila por já estar nela (ou já ter sido tratado) por outra linha.</summary>
    public const string RepeatedReason = "Repetido: este cliente tem uma conversa mais recente em outra linha.";

    /// <summary>
    /// Confere a fila de aprovação com o CRM, sem usar a IA. Sai quem: aparece duas vezes ou já foi tratado por uma conversa mais nova
    /// em outra linha; já foi respondido pela atendente; escreveu de novo (a busca escreve outra, se couber no período); ou já está
    /// finalizado. Devolve quantos finalizados saíram.
    /// </summary>
    public static async Task<int> RefreshAsync(PontualDb db, CrmClient crm, CrmAccess access, DateTime now, CancellationToken ct)
    {
        var count = 0;
        var all = await db.CrmReplies.AsNoTracking().Where(r => r.Kind == Kind && r.Status != CrmReplyStatus.Outdated).ToListAsync(ct);
        var byChat = all.ToLookup(r => r.ChatId);
        static bool Waiting(CrmReply r) => r.Status is CrmReplyStatus.Pending or CrmReplyStatus.Failed;
        foreach (var reply in all.Where(Waiting))
        {
            // Entre duas esperando, fica a da conversa mais recente; o Id desempata.
            var repeated = byChat[reply.ChatId].Any(o => o.Id != reply.Id && (o.SentAt > reply.WaitingSince
                || o.WaitingSince > reply.WaitingSince || o.WaitingSince == reply.WaitingSince && Waiting(o) && o.Id.CompareTo(reply.Id) > 0));
            if (repeated)
            {
                await CloseAsync(db, reply.Id, CrmReplyStatus.Outdated, RepeatedReason, now, ct);
                continue;
            }

            try
            {
                var history = Read(await crm.MessagesAsync(access, reply.ChatId, reply.BotId, ct));
                if (history.Count == 0)
                {
                    continue;
                }

                if (history[^1].WppId != reply.LastWppId)
                {
                    var who = history[^1].Who;
                    await CloseAsync(db, reply.Id, CrmReplyStatus.Outdated, history[^1].FromMe
                        ? $"{(who.Length > 0 ? who : "A equipe")} já respondeu este cliente no CRM."
                        : "O cliente escreveu de novo depois desta resposta.", now, ct);
                    continue;
                }

                if (Finished(history, await crm.StageAsync(access, reply.Phone, ct)) is { Length: > 0 } why)
                {
                    count += await CloseAsync(db, reply.Id, CrmReplyStatus.Skipped, why, now, ct);
                }
            }
            catch (CrmFailure)
            {
                // O CRM não respondeu: a resposta fica como está, e a aprovação confere de novo antes de enviar.
            }
        }

        return count;
    }

    /// <summary>Tira uma resposta da fila. Alguém pode ter aprovado enquanto a busca conferia: só mexe no que ainda espera.</summary>
    private static Task<int> CloseAsync(PontualDb db, Guid id, string status, string why, DateTime now, CancellationToken ct) =>
        db.CrmReplies.Where(r => r.Id == id && (r.Status == CrmReplyStatus.Pending || r.Status == CrmReplyStatus.Failed)).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, status)
            .SetProperty(r => r.Reason, r => status == CrmReplyStatus.Skipped ? why : r.Reason)
            .SetProperty(r => r.Error, r => status == CrmReplyStatus.Outdated ? why : r.Error)
            .SetProperty(r => r.UpdatedAt, now), ct);

    /// <summary>As últimas conversas que a equipe marcou como "Já foi finalizado", para a IA reconhecer as parecidas.</summary>
    private static async Task<string> FinishedExamplesAsync(PontualDb db, CancellationToken ct)
    {
        var marked = await db.CrmReplies.AsNoTracking()
            .Where(r => r.Kind == Kind && r.Status == CrmReplyStatus.Skipped && r.ReviewedByName != "")
            .OrderByDescending(r => r.UpdatedAt).Take(6).Select(r => r.Transcript).ToListAsync(ct);
        return marked.Count == 0 ? "" : "\n\n<ja_finalizados>\n"
            + string.Join("\n---\n", marked.Select(t => t.Length <= 600 ? t : "…" + t[^600..])) + "\n</ja_finalizados>";
    }

    /// <summary>
    /// Confere no CRM, antes de enviar, se a conversa ainda está como a IA leu. Devolve vazio se pode enviar, ou o motivo
    /// para não enviar (a atendente respondeu, ou o cliente escreveu de novo).
    /// </summary>
    public static async Task<string> ChangedAsync(CrmClient crm, CrmAccess access, CrmReply reply, CancellationToken ct)
    {
        var history = Read(await crm.MessagesAsync(access, reply.ChatId, reply.BotId, ct));
        var last = history.LastOrDefault(m => m.Text.Length > 0);
        if (last is null || last.WppId == reply.LastWppId)
        {
            return "";
        }

        return last.FromMe
            ? $"{(last.Who.Length > 0 ? last.Who : "A equipe")} já respondeu este cliente no CRM. Nada foi enviado."
            : "O cliente escreveu de novo depois desta resposta. Nada foi enviado: a próxima busca escreve outra.";
    }

    /// <summary>O histórico do CRM em falas, da mais velha para a mais nova, sem os avisos do sistema e as notas internas.</summary>
    public static List<Message> Read(JsonElement messages)
    {
        var list = new List<Message>();
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = Str(m, "type");
            var who = Str(m, "sent_by_name");
            if (SystemTypes.Contains(type) || who.Equals("Sistema", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = Plain(Str(m, "body"));
            if (Str(m, "transcription") is { Length: > 0 } transcription)
            {
                text = $"[áudio] {transcription}";
            }
            else if (type is not ("chat" or "text" or "") && (text.Length == 0 || text.StartsWith('(')))
            {
                text = text.Length > 0 && !text.StartsWith('(') ? $"[{type}] {text}" : $"[{type}]";
            }

            if (text.Length == 0)
            {
                continue;
            }

            var at = m.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.MinValue;
            var fromMe = m.TryGetProperty("is_from_me", out var f) && f.ValueKind == JsonValueKind.True;
            list.Add(new Message(at, fromMe, fromMe ? who : "", text.Length <= CrmAgent.MaxText ? text : text[..CrmAgent.MaxText], Str(m, "wpp_id")));
        }

        list.Sort((a, b) => a.At.CompareTo(b.At));
        return list;
    }

    private static string Plain(string html) =>
        WebUtility(Regex.Replace(html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase), "<[^>]+>", "")).Trim();

    private static string WebUtility(string text) => System.Net.WebUtility.HtmlDecode(text);

    private static string Str(JsonElement scope, string name) =>
        scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" }
            : "";

    /// <summary>Lê a data do CRM: "last_message_at" vem em UTC (com Z); o "ts" de cada linha vem na hora de Brasília, sem fuso.</summary>
    private static DateTime? Time(string text, bool utc)
    {
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return null;
        }

        if (text.EndsWith('Z') || text.Contains('+') || utc && parsed.Kind != DateTimeKind.Unspecified)
        {
            return DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), AssistantPrompt.BrazilTime);
    }
}

/// <summary>Roda a busca dos clientes perdidos a cada meia hora, das 8h às 18h (horário de Brasília), de segunda a sábado.</summary>
public sealed class CrmLostWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<CrmLostWorker> logger) : BackgroundService
{
    private static readonly SemaphoreSlim Running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), time, stoppingToken);
            await TidyAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, AssistantPrompt.BrazilTime);
                if (local.DayOfWeek != DayOfWeek.Sunday && local.Hour is >= 8 and < 18 && !await RanRecentlyAsync(stoppingToken))
                {
                    await RunAsync(scopes, time, logger, force: false, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromMinutes(30), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Ao ligar a Central: confere a fila com o CRM (sem IA). Se a tela ainda não guardou período nenhum e a última busca
    /// foi feita com datas escolhidas, são elas que passam a valer.
    /// </summary>
    private async Task TidyAsync(CancellationToken ct)
    {
        if (!await Running.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            if (!await db.TextSettings.AnyAsync(s => s.Key == TextSetting.CrmLostPeriod, ct))
            {
                var last = await db.TextSettings.AsNoTracking().Where(s => s.Key == TextSetting.CrmLostLastRun).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? "";
                if (CrmLost.PeriodOfLastRun(last) is { Length: > 0 } period)
                {
                    db.TextSettings.Add(new TextSetting { Key = TextSetting.CrmLostPeriod, Value = period });
                    await db.SaveChangesAsync(ct);
                }
            }

            if (await CrmClient.AccessAsync(db, ct) is { } access)
            {
                await CrmLost.RefreshAsync(db, scope.ServiceProvider.GetRequiredService<CrmClient>(), access, time.GetUtcNow().UtcDateTime, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao conferir a fila dos clientes perdidos do CRM");
        }
        finally
        {
            Running.Release();
        }
    }

    /// <summary>Houve busca há menos de 25 minutos: a Central reiniciada (atualização) não busca de novo fora de hora.</summary>
    private async Task<bool> RanRecentlyAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var last = await scope.ServiceProvider.GetRequiredService<PontualDb>().TextSettings.AsNoTracking()
            .Where(s => s.Key == TextSetting.CrmLostLastRun).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? "";
        return DateTime.TryParse(last.Split('|')[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            && time.GetUtcNow().UtcDateTime - at < TimeSpan.FromMinutes(25);
    }

    /// <summary>Uma busca, uma de cada vez; grava quando rodou e o que achou para a tela.</summary>
    public static async Task<CrmLost.Result> RunAsync(IServiceScopeFactory scopes, TimeProvider time, ILogger logger, bool force, CancellationToken ct,
        DateTime? fromDay = null, DateTime? toDay = null)
    {
        if (!await Running.WaitAsync(0, ct))
        {
            return new CrmLost.Result(0, 0, "Já há uma busca rodando; espere ela terminar.");
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            CrmLost.Result result;
            try
            {
                result = await CrmLost.ScanAsync(scope.ServiceProvider, force, ct, fromDay, toDay);
            }
            catch (CrmFailure failure)
            {
                result = new CrmLost.Result(0, 0, $"A busca parou: {failure.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha na busca dos clientes perdidos do CRM");
                result = new CrmLost.Result(0, 0, "A busca falhou por um erro interno; está no registro da Central.");
            }

            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            var value = $"{time.GetUtcNow().UtcDateTime:O}|{result.Checked}|{result.Created}|{result.Note}";
            var row = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == TextSetting.CrmLostLastRun, ct);
            if (row is null)
            {
                db.TextSettings.Add(new TextSetting { Key = TextSetting.CrmLostLastRun, Value = value });
            }
            else
            {
                row.Value = value;
            }

            await db.SaveChangesAsync(ct);
            return result;
        }
        finally
        {
            Running.Release();
        }
    }
}
