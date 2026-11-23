using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Consulta de CPF no Nexus pelo chat da IA. A atendente manda o CPF; o chat mostra os botões SIAPE, INSS e Exército
/// (um já sugerido); no clique a Central consulta a API de integração do Nexus e o resultado do roteiro aparece na
/// conversa. Consulta paga só com a confirmação da atendente. O resultado fica guardado 24 horas: o mesmo CPF no mesmo
/// setor, nesse prazo, sai daqui sem chamar o Nexus.
/// </summary>
public static class NexusEndpoints
{
    private static readonly Permission Access = Permission.ConfigEdit;
    /// <summary>Quanto tempo o resultado fica guardado e é reaproveitado.</summary>
    internal static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    /// <summary>Depois disso, uma consulta que não terminou é dada como perdida.</summary>
    private static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(4);

    private sealed record ConsultBody(string? Cpf, string? Setor, bool? AllowPaid, bool? NewConversation);
    private sealed record SettingsBody(string? Url, string? Token, bool? RemoveToken);

    public static void Map(RouteGroupBuilder api)
    {
        // Terminal (chat da IA). Não chamam a IA: o limite é o comum das máquinas.
        api.MapGet("/device/nexus/localizar", LocateAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPost("/device/nexus/consultas", ConsultAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapGet("/device/nexus/consultas/{id:guid}", FollowAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);

        // Central Neural.
        api.MapGet("/neural/nexus", OverviewAsync).Panel(Access);
        api.MapPut("/neural/nexus/settings", SaveSettingsAsync).Panel(Access);
        api.MapPost("/neural/nexus/test", TestAsync).Panel(Access);
    }

    // ------------------------------------------------------------------ terminal

    private static async Task<Device> AllowedAsync(HttpContext context, PontualDb db, IConfiguration configuration, CancellationToken ct)
    {
        var device = context.Device();
        if (!await AssistantEndpoints.IsEnabledAsync(db, configuration, device, ct))
        {
            throw ApiError.Conflict("A assistente não está ligada para este computador.");
        }

        return device;
    }

    /// <summary>
    /// O que o chat precisa para desenhar os botões: estado de cada setor, se a consulta é grátis, já guardada ou paga,
    /// e qual setor sugerir. A sugestão vem, nesta ordem, do que a atendente escreveu junto do CPF, de uma consulta já
    /// guardada na Central e do que o Nexus diz de graça (base SIAPE, cache do INSS e do Exército).
    /// </summary>
    private static async Task<IResult> LocateAsync(HttpContext context, PontualDb db, IConfiguration configuration, NexusClient nexus, TimeProvider time,
        string? cpf, string? dica, CancellationToken ct)
    {
        await AllowedAsync(context, db, configuration, ct);
        var access = await NexusClient.AccessAsync(db, ct);
        if (access is null)
        {
            return Results.Ok(new { configured = false });
        }

        var number = NexusClient.NormalizeCpf(cpf) ?? throw ApiError.BadRequest("CPF inválido. Confira os 11 dígitos.");
        var now = time.GetUtcNow().UtcDateTime;
        await ForgetOldAsync(db, now, ct);
        var saved = await SavedAsync(db, number, null, now, ct);

        string? offline = null;
        JsonElement health = default, located = default;
        try
        {
            var h = await nexus.HealthAsync(access, ct);
            if (h.Status == 200)
            {
                health = h.Body;
            }
            else
            {
                offline = Friendly(h);
            }

            var l = await nexus.LocateAsync(access, number, ct);
            if (l.Status == 200)
            {
                located = l.Body;
            }
        }
        catch (NexusOffline failure)
        {
            offline = failure.Message;
        }

        var setores = NexusClient.Setores.Select(setor =>
        {
            var estado = offline is not null ? "fora" : Prop(Prop(health, "setores"), setor) is { ValueKind: JsonValueKind.String } s ? s.GetString()! : "ok";
            var motivo = offline ?? (Prop(Prop(health, "motivos"), setor) is { ValueKind: JsonValueKind.String } m ? m.GetString() : null);
            var guardada = saved.FirstOrDefault(c => c.Setor == setor);
            var (custo, nota) = guardada is not null
                ? ("guardado", $"já consultado às {Local(guardada.CompletedAt ?? guardada.CreatedAt)}, sem custo")
                : setor switch
                {
                    "siape" => ("gratis", "grátis"),
                    "inss" when Flag(located, "inss_cache") => ("guardado", "consultado nos últimos 7 dias, sem custo"),
                    "exercito" when Flag(located, "exercito_cache") => ("guardado", "consultado nas últimas 24 h, sem custo"),
                    _ => ("paga", "consulta paga"),
                };
            return new { setor, nome = NexusSummary.SetorName(setor), estado, motivo, custo, nota };
        }).ToList();

        var usable = setores.Where(s => s.estado != "fora").Select(s => s.setor).ToHashSet();
        string?[] candidates =
        [
            Hint(dica),
            saved.FirstOrDefault()?.Setor,
            Flag(located, "siape") ? "siape" : null,
            Flag(located, "inss_cache") ? "inss" : null,
            Flag(located, "exercito_cache") ? "exercito" : null,
        ];
        var sugestao = candidates.FirstOrDefault(c => c is not null && usable.Contains(c));

        return Results.Ok(new
        {
            configured = true,
            cpf = NexusClient.FormatCpf(number),
            nome = Prop(located, "nome") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null,
            naEsteira = Flag(located, "na_esteira"),
            sugestao,
            setores,
            aviso = offline,
        });
    }

    private static async Task<IResult> ConsultAsync(HttpContext context, PontualDb db, IConfiguration configuration, NexusClient nexus, TimeProvider time, CancellationToken ct)
    {
        var device = await AllowedAsync(context, db, configuration, ct);
        var access = await NexusClient.AccessAsync(db, ct)
            ?? throw ApiError.Conflict("A consulta no Nexus ainda não foi configurada na Central. Avise o TI.");
        var body = await context.ReadBodyAsync<ConsultBody>();
        var cpf = NexusClient.NormalizeCpf(body.Cpf) ?? throw ApiError.BadRequest("CPF inválido. Confira os 11 dígitos.");
        var setor = (body.Setor ?? "").Trim().ToLowerInvariant();
        if (!NexusClient.Setores.Contains(setor))
        {
            throw ApiError.BadRequest("Escolha o setor: SIAPE, INSS ou Exército.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        await ForgetOldAsync(db, now, ct);
        var row = new NexusConsulta
        {
            Id = Guid.NewGuid(),
            DeviceId = device.Id,
            AttendantName = device.Label,
            Cpf = cpf,
            Setor = setor,
            Status = NexusStatus.Running,
            CreatedAt = now,
            ConversationId = await ConversationAsync(db, device, body.NewConversation == true, now, ct),
        };
        var question = $"Consultar no Nexus: CPF {NexusClient.FormatCpf(cpf)} — {NexusSummary.SetorName(setor)}";

        // Já consultado nas últimas 24 horas: responde daqui, sem chamar o Nexus.
        var saved = (await SavedAsync(db, cpf, setor, now, ct)).FirstOrDefault();
        if (saved is not null)
        {
            row.Status = saved.Status;
            row.FromCache = true;
            row.NexusId = saved.NexusId;
            row.ResultJson = saved.ResultJson;
            row.CompletedAt = now;
            using var document = JsonDocument.Parse(saved.ResultJson);
            var at = saved.CompletedAt ?? saved.CreatedAt;
            row.Summary = saved.Status == NexusStatus.Done
                ? NexusSummary.Build(document.RootElement, setor, fromCentral: true, at)
                : NexusSummary.NotFound(cpf, setor, Text(document.RootElement, "mensagem"), paga: false, fromCentral: true, at);
            db.NexusConsultas.Add(row);
            AddQuestion(db, row, question, now);
            return Results.Ok(await FinishAsync(db, row, time, ct));
        }

        NexusReply reply;
        try
        {
            reply = await nexus.ConsultAsync(access, cpf, setor, device.Id.ToString(), Attendant(device), body.AllowPaid == true, ct);
        }
        catch (NexusOffline failure)
        {
            return Results.Ok(new { status = "erro", mensagem = failure.Message });
        }

        // Seria cobrada e a atendente ainda não confirmou: nada é registrado, o chat pede a confirmação.
        if (reply.Status == 402)
        {
            return Results.Ok(new { status = "precisa_autorizar", mensagem = "Esta consulta é paga: ainda não há resultado guardado deste CPF neste setor. Confirme para consultar." });
        }

        db.NexusConsultas.Add(row);
        // A pergunta entra antes da resposta (a ordem das falas é a dos IDs); se a consulta falhar, ela sai.
        var asked = AddQuestion(db, row, question, now);
        var outcome = await ApplyAsync(db, row, reply, time, ct);
        if (row.Status == NexusStatus.Failed)
        {
            db.AssistantMessages.Remove(asked);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(outcome);
    }

    private static async Task<IResult> FollowAsync(Guid id, HttpContext context, PontualDb db, IConfiguration configuration, NexusClient nexus, TimeProvider time, CancellationToken ct)
    {
        var device = await AllowedAsync(context, db, configuration, ct);
        var row = await db.NexusConsultas.FirstOrDefaultAsync(c => c.Id == id && c.DeviceId == device.Id, ct)
            ?? throw ApiError.NotFound("Consulta não encontrada.");
        if (row.Status != NexusStatus.Running)
        {
            return Results.Ok(await DoneAsync(db, row, ct));
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (now - row.CreatedAt > GiveUp)
        {
            return Results.Ok(await FailAsync(db, row, "A consulta demorou demais no Nexus. Clique de novo: se a fonte já respondeu, não cobra outra vez.", ct));
        }

        var access = await NexusClient.AccessAsync(db, ct)
            ?? throw ApiError.Conflict("A consulta no Nexus ainda não foi configurada na Central. Avise o TI.");
        NexusReply reply;
        try
        {
            reply = await nexus.FollowAsync(access, row.NexusId, ct);
        }
        catch (NexusOffline failure)
        {
            // Pode ser só um soluço da rede: a consulta continua no Nexus e o chat tenta de novo.
            return Results.Ok(new { id = row.Id, status = NexusStatus.Running, previsaoSegundos = 5, mensagem = failure.Message });
        }

        var outcome = await ApplyAsync(db, row, reply, time, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(outcome);
    }

    /// <summary>Interpreta a resposta do Nexus (do POST ou do acompanhamento) e atualiza a consulta.</summary>
    private static async Task<object> ApplyAsync(PontualDb db, NexusConsulta row, NexusReply reply, TimeProvider time, CancellationToken ct)
    {
        var body = reply.Body;
        var status = reply.Text("status");
        var codigo = reply.Text("codigo");
        if (reply.Text("consulta_id") is { Length: > 0 } nexusId)
        {
            row.NexusId = nexusId;
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (reply.Status == 200 && status == "concluida")
        {
            var fonte = Prop(body, "fonte");
            row.Status = NexusStatus.Done;
            row.Paga = Flag(fonte, "paga");
            row.FromCache = Text(fonte, "origem") == "cache";
            row.ResultJson = body.GetRawText();
            row.CompletedAt = now;
            row.Summary = NexusSummary.Build(body, row.Setor, fromCentral: false, null);
            return await FinishAsync(db, row, time, ct);
        }

        if (codigo == "cpf_nao_encontrado")
        {
            var fonte = Prop(body, "fonte");
            row.Status = NexusStatus.NotFound;
            row.Paga = Flag(fonte, "paga");
            row.FromCache = Text(fonte, "origem") == "cache";
            row.ResultJson = JsonSerializer.Serialize(new { codigo, mensagem = reply.Text("mensagem") });
            row.CompletedAt = now;
            row.Summary = NexusSummary.NotFound(row.Cpf, row.Setor, reply.Text("mensagem"), row.Paga, fromCentral: false, null);
            return await FinishAsync(db, row, time, ct);
        }

        // 202, acompanhamento ainda andando, ou 409 (já havia uma consulta igual andando: segue a dela).
        if (reply.Status == 202 || (reply.Status == 200 && status == NexusStatus.Running) || (codigo == "consulta_em_andamento" && row.NexusId.Length > 0))
        {
            row.Status = NexusStatus.Running;
            var previsao = Prop(body, "previsao_segundos") is { ValueKind: JsonValueKind.Number } p && p.TryGetInt32(out var s) ? s : 30;
            return new { id = row.Id, status = NexusStatus.Running, previsaoSegundos = previsao };
        }

        row.Status = NexusStatus.Failed;
        row.Error = Friendly(reply);
        row.CompletedAt = now;
        return new { id = row.Id, status = NexusStatus.Failed, mensagem = row.Error };
    }

    /// <summary>Grava o resultado como uma fala da assistente na conversa (a IA o lê nas próximas perguntas).</summary>
    private static async Task<object> FinishAsync(PontualDb db, NexusConsulta row, TimeProvider time, CancellationToken ct)
    {
        var answer = new AssistantMessage
        {
            DeviceId = row.DeviceId,
            ConversationId = row.ConversationId,
            Role = AssistantRole.Assistant,
            Text = row.Summary,
            At = time.GetUtcNow().UtcDateTime,
        };
        db.AssistantMessages.Add(answer);
        await db.SaveChangesAsync(ct);
        row.MessageId = answer.Id;
        await db.SaveChangesAsync(ct);
        return Answer(row, answer);
    }

    private static async Task<object> DoneAsync(PontualDb db, NexusConsulta row, CancellationToken ct)
    {
        if (row.Status == NexusStatus.Failed)
        {
            return new { id = row.Id, status = NexusStatus.Failed, mensagem = row.Error };
        }

        var answer = row.MessageId is { } messageId ? await db.AssistantMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct) : null;
        return answer is null ? new { id = row.Id, status = row.Status, mensagem = "O resultado já foi mostrado na conversa." } : Answer(row, answer);
    }

    private static object Answer(NexusConsulta row, AssistantMessage answer) =>
        new { id = row.Id, status = row.Status, conversationId = row.ConversationId, resposta = new { id = answer.Id, text = answer.Text, at = answer.At } };

    private static async Task<object> FailAsync(PontualDb db, NexusConsulta row, string message, CancellationToken ct)
    {
        row.Status = NexusStatus.Failed;
        row.Error = message;
        await db.SaveChangesAsync(ct);
        return new { id = row.Id, status = NexusStatus.Failed, mensagem = message };
    }

    private static AssistantMessage AddQuestion(PontualDb db, NexusConsulta row, string text, DateTime now)
    {
        var message = new AssistantMessage { DeviceId = row.DeviceId, ConversationId = row.ConversationId, Role = AssistantRole.User, Text = text, At = now };
        db.AssistantMessages.Add(message);
        return message;
    }

    /// <summary>A conversa atual do chat, com a mesma regra das perguntas (nova depois de 8 horas parada).</summary>
    private static async Task<Guid> ConversationAsync(PontualDb db, Device device, bool newConversation, DateTime now, CancellationToken ct)
    {
        var last = await db.AssistantMessages.AsNoTracking().Where(m => m.DeviceId == device.Id).OrderByDescending(m => m.Id).FirstOrDefaultAsync(ct);
        return newConversation || last is null || now - last.At > AssistantEndpoints.ConversationIdle ? Guid.NewGuid() : last.ConversationId;
    }

    /// <summary>Consultas deste CPF terminadas nas últimas 24 horas e ainda guardadas, a mais nova primeiro (uma por setor).</summary>
    private static async Task<List<NexusConsulta>> SavedAsync(PontualDb db, string cpf, string? setor, DateTime now, CancellationToken ct)
    {
        var since = now - Keep;
        var rows = await db.NexusConsultas.AsNoTracking()
            .Where(c => c.Cpf == cpf && (setor == null || c.Setor == setor) && c.CreatedAt > since && c.ResultJson != ""
                && (c.Status == NexusStatus.Done || c.Status == NexusStatus.NotFound))
            .ToListAsync(ct);
        return rows.OrderByDescending(c => c.CompletedAt ?? c.CreatedAt).GroupBy(c => c.Setor).Select(g => g.First()).ToList();
    }

    /// <summary>Passadas 24 horas, o resultado é apagado; fica só o registro de quem consultou o quê.</summary>
    private static Task ForgetOldAsync(PontualDb db, DateTime now, CancellationToken ct)
    {
        var limit = now - Keep;
        return db.NexusConsultas.Where(c => c.CreatedAt < limit && (c.ResultJson != "" || c.Summary != ""))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResultJson, "").SetProperty(c => c.Summary, ""), ct);
    }

    private static string Attendant(Device device) => string.IsNullOrWhiteSpace(device.AttendantName) ? device.Hostname : device.AttendantName;

    /// <summary>Setor citado pela atendente junto do CPF ("123..., INSS").</summary>
    public static string? Hint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var plain = new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        bool Any(params string[] words) => words.Any(plain.Contains);
        if (Any("exercito", "militar", "sargento", "soldado", "tenente", "coronel", "capitao", "consigbr"))
        {
            return "exercito";
        }

        if (Any("siape", "servidor", "sougov", "federal", "orgao", "upag"))
        {
            return "siape";
        }

        return Any("inss", "aposentad", "pensionista", "beneficio", "loas", "bpc") ? "inss" : null;
    }

    /// <summary>Mensagem do Nexus para a atendente; os problemas de configuração viram "avise o TI".</summary>
    private static string Friendly(NexusReply reply)
    {
        var codigo = reply.Text("codigo");
        var mensagem = reply.Text("mensagem").Trim();
        return codigo switch
        {
            "token_invalido" or "ip_nao_autorizado" or "integracao_nao_configurada" =>
                $"A ligação da Central com o Nexus está com problema de configuração ({(mensagem.Length > 0 ? mensagem.TrimEnd('.') : codigo)}). Avise o TI.",
            "interrompida" => "A consulta foi interrompida porque o Nexus reiniciou. Clique de novo: se a fonte já tinha respondido, não cobra.",
            "consulta_nao_encontrada" => "O Nexus não tem mais esta consulta. Clique de novo para consultar.",
            "setor_indisponivel" => $"{(mensagem.Length > 0 ? mensagem.TrimEnd('.') : "O setor está fora do ar no Nexus")}. Tente de novo em alguns minutos.",
            "muitas_requisicoes" => "O Nexus está recebendo pedidos demais. Aguarde um minuto e tente de novo.",
            _ => mensagem.Length > 0 ? mensagem : $"O Nexus respondeu com o erro {reply.Status}.",
        };
    }

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    private static string Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Brasilia.Value).ToString("HH:mm", Br);

    private static readonly Lazy<TimeZoneInfo> Brasilia = new(() =>
    {
        foreach (var zone in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(zone);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
    });

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static bool Flag(JsonElement e, string name) => Prop(e, name).ValueKind == JsonValueKind.True;

    private static string Text(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";

    // ------------------------------------------------------------------ Central Neural

    private static async Task<IResult> OverviewAsync(PontualDb db, TimeProvider time, CancellationToken ct)
    {
        var settings = await db.TextSettings.AsNoTracking().Where(s => s.Key.StartsWith("nexus.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var since = time.GetUtcNow().UtcDateTime - Keep;
        var today = await db.NexusConsultas.AsNoTracking().Where(c => c.CreatedAt > since).Select(c => new { c.Paga, c.FromCache, c.Status }).ToListAsync(ct);
        var recent = await db.NexusConsultas.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(100)
            .Select(c => new { id = c.Id, at = c.CreatedAt, attendant = c.AttendantName, cpf = c.Cpf, setor = c.Setor, status = c.Status, paga = c.Paga, fromCache = c.FromCache, error = c.Error })
            .ToListAsync(ct);
        return Results.Ok(new
        {
            url = settings.GetValueOrDefault(TextSetting.NexusUrl, ""),
            defaultUrl = NexusClient.DefaultUrl,
            hasToken = settings.GetValueOrDefault(TextSetting.NexusToken, "").Length > 0,
            last24h = new { total = today.Count, pagas = today.Count(c => c.Paga), guardadas = today.Count(c => c.FromCache), erros = today.Count(c => c.Status == NexusStatus.Failed) },
            recent = recent.Select(c => new { c.id, c.at, c.attendant, cpf = NexusClient.FormatCpf(c.cpf), c.setor, c.status, c.paga, c.fromCache, c.error }),
        });
    }

    private static async Task<IResult> SaveSettingsAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<SettingsBody>();
        var url = Input.Truncated(body.Url, 200).TrimEnd('/');
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            throw ApiError.BadRequest("O endereço do Nexus precisa começar com http:// ou https://.");
        }

        await SetAsync(db, TextSetting.NexusUrl, url, ct);
        var token = (body.Token ?? "").Trim();
        if (body.RemoveToken == true)
        {
            await SetAsync(db, TextSetting.NexusToken, "", ct);
        }
        else if (token.Length > 0)
        {
            if (token.Length > 500 || token.Any(char.IsWhiteSpace))
            {
                throw ApiError.BadRequest("O token não pode ter espaços nem quebras de linha. Copie de novo, só o token.");
            }

            await SetAsync(db, TextSetting.NexusToken, AssistantSecrets.Protect(token), ct);
        }

        await audit.SaveAsync(db, AuditInfo.By(admin, "nexus.settings",
            $"{admin.Name} salvou a ligação com o Nexus{(body.RemoveToken == true ? " (token removido)" : token.Length > 0 ? " (token novo)" : "")}"), cancellationToken: ct);
        return await OverviewAsync(db, time, ct);
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

    private static async Task<IResult> TestAsync(PontualDb db, NexusClient nexus, CancellationToken ct)
    {
        var access = await NexusClient.AccessAsync(db, ct) ?? throw ApiError.BadRequest("Cole o token do Nexus e salve antes de testar.");
        NexusReply reply;
        try
        {
            reply = await nexus.HealthAsync(access, ct, fresh: true);
        }
        catch (NexusOffline failure)
        {
            throw ApiError.Conflict(failure.Message);
        }

        if (reply.Status != 200)
        {
            throw ApiError.Conflict(Friendly(reply));
        }

        var parts = NexusClient.Setores.Select(setor =>
        {
            var estado = Text(Prop(reply.Body, "setores"), setor);
            var motivo = Text(Prop(reply.Body, "motivos"), setor);
            return $"{NexusSummary.SetorName(setor)} {(estado.Length > 0 ? estado : "?")}{(motivo.Length > 0 ? $" ({motivo})" : "")}";
        });
        return Results.Ok(new { ok = true, message = $"Ligação com o Nexus funcionando: {string.Join(", ", parts)}." });
    }
}
