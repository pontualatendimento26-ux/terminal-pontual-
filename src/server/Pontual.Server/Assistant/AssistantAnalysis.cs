using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Audit;
using Pontual.Server.Api;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

public sealed record AnalysisResult(int MessagesRead, int NewSuggestions, int UpdatedSuggestions, bool More);

/// <summary>
/// A IA lê as conversas novas de uma atendente com a assistente e devolve duas coisas: o perfil de atendimento
/// dela (para a supervisão) e sugestões de conhecimento (lacunas, informações novas, conteúdo a revisar).
/// Só as falas ainda não analisadas são enviadas; o perfil anterior vai junto para ser atualizado, não refeito.
/// As sugestões ficam pendentes: nada chega à assistente sem a aprovação de um administrador.
/// </summary>
public static class AssistantAnalysis
{
    /// <summary>Com menos falas novas que isto, a análise automática espera juntar mais.</summary>
    public const int MinNewMessages = 4;

    private const int BatchMessages = 300;
    private const int BatchChars = 120_000;
    private const int AnswerShown = 1500;
    private const int NewSuggestionsPerRun = 8;

    /// <summary>Uma análise por vez no servidor: evita duas chamadas pagas sobre as mesmas falas.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private const string Instructions =
        """
        Você apoia a supervisão da Pontual Consig, um correspondente bancário de crédito consignado. As atendentes da Pontual operam os portais dos bancos e tiram dúvidas com uma assistente interna de IA, que responde com base na base de conhecimento da empresa (roteiros, documentos e conhecimentos).

        Você recebe as conversas recentes de UMA atendente com essa assistente e faz duas entregas.

        1. O perfil de atendimento dela, que a supervisão lê para decidir treinamento e para orientar a assistente a ajudá-la melhor.
        Você só enxerga o que ela pergunta à assistente, não o atendimento ao cliente em si. Descreva o que as conversas mostram: com quais bancos e operações ela trabalha, que tipo de dúvida traz, onde tem dificuldade recorrente, o que já domina, como costuma perguntar. Se houver um perfil anterior, atualize-o com o que as conversas novas acrescentam, mantendo o que continua valendo; em "bancos", some as perguntas novas às contagens anteriores. Com poucas conversas, diga que a base ainda é pequena em vez de tirar conclusões fortes. Escreva de forma respeitosa e objetiva: é um retrato de trabalho, não uma avaliação da pessoa.

        2. Sugestões para melhorar a base de conhecimento, apenas quando as conversas derem motivo:
        - "gap": a atendente perguntou algo que a base não cobre (a assistente disse que não tinha a informação, ou respondeu só com conhecimento geral).
        - "new_info": a atendente contou um fato de trabalho que não está na base (uma regra que mudou, um procedimento do banco).
        - "review": a atendente contestou uma resposta que veio da base, ou a resposta se mostrou confusa ou desatualizada.
        Se o assunto já aparece na lista de sugestões pendentes, não crie outra: devolva o número dela em "sugestao_existente" (e 0 quando for nova). Em "conteudo_sugerido", rascunhe o texto do conhecimento somente com o que a conversa afirma; nunca complete com taxa, prazo ou regra que ninguém disse. Quando a conversa só revela a dúvida, deixe "conteudo_sugerido" vazio e explique em "detalhe" o que precisa ser escrito e por quem confirmar. Nenhuma sugestão é melhor do que uma sugestão fraca: a lista pode vir vazia.

        Não copie dados de clientes (nome, CPF, número de benefício, telefone, conta) para o perfil nem para as sugestões. Escreva tudo em português.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = BuildSchema();

    private static Dictionary<string, JsonElement> BuildSchema()
    {
        object text = new { type = "string" };
        object number = new { type = "integer" };
        object list = new { type = "array", items = text };

        var profile = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "resumo", "estilo", "experiencia", "bancos", "temas", "dificuldades", "pontos_fortes", "recomendacoes" },
            properties = new
            {
                resumo = text,
                estilo = text,
                experiencia = new { type = "string", @enum = new[] { "iniciante", "intermediaria", "experiente", "indefinida" } },
                bancos = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "nome", "perguntas" },
                        properties = new { nome = text, perguntas = number },
                    },
                },
                temas = list,
                dificuldades = list,
                pontos_fortes = list,
                recomendacoes = list,
            },
        };

        var suggestion = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "tipo", "titulo", "detalhe", "banco", "conteudo_sugerido", "pergunta_exemplo", "ocorrencias", "sugestao_existente" },
            properties = new
            {
                tipo = new { type = "string", @enum = new[] { SuggestionKind.Gap, SuggestionKind.NewInfo, SuggestionKind.Review } },
                titulo = text,
                detalhe = text,
                banco = text,
                conteudo_sugerido = text,
                pergunta_exemplo = text,
                ocorrencias = number,
                sugestao_existente = number,
            },
        };

        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "perfil", "sugestoes" }),
            ["properties"] = JsonSerializer.SerializeToElement(new { perfil = profile, sugestoes = new { type = "array", items = suggestion } }),
        };
    }

    /// <summary>Quantas falas de cada máquina ainda não passaram pela análise.</summary>
    public static async Task<Dictionary<Guid, int>> PendingAsync(PontualDb db, CancellationToken ct)
    {
        var through = await db.AttendantInsights.AsNoTracking().ToDictionaryAsync(i => i.DeviceId, i => i.AnalyzedThroughMessageId, ct);
        var messages = await db.AssistantMessages.AsNoTracking().Select(m => new { m.DeviceId, m.Id }).ToListAsync(ct);
        return messages
            .Where(m => m.Id > through.GetValueOrDefault(m.DeviceId))
            .GroupBy(m => m.DeviceId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Analisa um lote de falas novas da atendente. <paramref name="admin"/> nulo = análise automática.
    /// Falha da IA sai como <see cref="AssistantFailure"/>; nesse caso nada é gravado e as falas ficam para a próxima.
    /// </summary>
    public static async Task<AnalysisResult> RunAsync(
        PontualDb db, IAssistantModel model, IConfiguration configuration, AuditWriter audit, TimeProvider time,
        Guid deviceId, Admin? admin, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                ?? throw ApiError.NotFound("Máquina não encontrada.");
            var apiKey = await AssistantSecrets.ReadKeyAsync(db, configuration, ct)
                ?? throw ApiError.Conflict("Cadastre a chave da IA antes de analisar as conversas.");

            var insight = await db.AttendantInsights.FirstOrDefaultAsync(i => i.DeviceId == deviceId, ct);
            var through = insight?.AnalyzedThroughMessageId ?? 0;
            var fresh = await db.AssistantMessages.AsNoTracking()
                .Where(m => m.DeviceId == deviceId && m.Id > through)
                .OrderBy(m => m.Id)
                .Take(BatchMessages + 1)
                .ToListAsync(ct);
            if (fresh.Count == 0)
            {
                return new AnalysisResult(0, 0, 0, false);
            }

            var conversations = new StringBuilder();
            var batch = new List<AssistantMessage>();
            Guid? conversation = null;
            foreach (var message in fresh.Take(BatchMessages))
            {
                // As respostas longas vão resumidas ao começo: o que importa aqui é a dúvida e se houve resposta.
                var said = message.Role == AssistantRole.Assistant && message.Text.Length > AnswerShown
                    ? message.Text[..AnswerShown] + " […]"
                    : message.Text;
                if (batch.Count > 0 && conversations.Length + said.Length > BatchChars)
                {
                    break;
                }

                if (message.ConversationId != conversation)
                {
                    conversation = message.ConversationId;
                    conversations.Append("--- conversa iniciada em ").Append(message.At.ToString("yyyy-MM-dd HH:mm")).Append(" (UTC) ---\n");
                }

                conversations.Append(message.Role == AssistantRole.User ? "ATENDENTE: " : "ASSISTENTE: ").Append(said).Append("\n\n");
                batch.Add(message);
            }

            var more = batch.Count < fresh.Count;
            var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            var modelId = AssistantEndpoints.ModelOf(settings);

            var knowledge = await db.AssistantScripts.AsNoTracking().Where(s => s.Enabled)
                .OrderBy(s => s.Title).Select(s => new { s.Kind, s.Title, s.Bank }).Take(600).ToListAsync(ct);
            var pending = await db.KnowledgeSuggestions
                .Where(s => s.Status == SuggestionStatus.Pending)
                .OrderByDescending(s => s.UpdatedAt).Take(80).ToListAsync(ct);

            var input = new StringBuilder()
                .Append("<atendente>").Append(device.Label).Append("</atendente>\n\n")
                .Append("<perfil_anterior>\n").Append(insight is { ProfileJson.Length: > 0 } ? insight.ProfileJson : "Ainda não há perfil.").Append("\n</perfil_anterior>\n\n")
                .Append("<base_de_conhecimento>\n");
            foreach (var item in knowledge)
            {
                input.Append("- [").Append(KnowledgeKind.Label(item.Kind)).Append("] ").Append(item.Title);
                if (item.Bank.Length > 0)
                {
                    input.Append(" (").Append(item.Bank).Append(')');
                }

                input.Append('\n');
            }

            input.Append("</base_de_conhecimento>\n\n<sugestoes_pendentes>\n");
            for (var i = 0; i < pending.Count; i++)
            {
                input.Append(i + 1).Append(". ").Append(pending[i].Title).Append('\n');
            }

            if (pending.Count == 0)
            {
                input.Append("Nenhuma.\n");
            }

            input.Append("</sugestoes_pendentes>\n\n<conversas>\n").Append(conversations).Append("</conversas>");

            var json = await model.AskJsonAsync(new AssistantJsonRequest(apiKey, modelId, Instructions, input.ToString(), Schema), ct);

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(json).RootElement;
                _ = root.GetProperty("perfil").GetProperty("resumo").GetString();
                _ = root.GetProperty("sugestoes").GetArrayLength();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new AssistantFailure("A análise voltou em um formato inesperado. Tente de novo.", ex);
            }

            var now = time.GetUtcNow().UtcDateTime;
            if (insight is null)
            {
                insight = new AttendantInsight { DeviceId = deviceId };
                db.AttendantInsights.Add(insight);
            }

            insight.ProfileJson = root.GetProperty("perfil").GetRawText();
            insight.AnalyzedThroughMessageId = batch[^1].Id;
            insight.MessagesAnalyzed += batch.Count;
            insight.UpdatedAt = now;
            insight.Model = modelId;
            insight.LastError = "";

            int created = 0, updated = 0;
            foreach (var suggested in root.GetProperty("sugestoes").EnumerateArray())
            {
                var existing = Number(suggested, "sugestao_existente");
                var occurrences = Math.Clamp(Number(suggested, "ocorrencias"), 1, 50);
                if (existing >= 1 && existing <= pending.Count)
                {
                    pending[existing - 1].Occurrences += occurrences;
                    pending[existing - 1].UpdatedAt = now;
                    updated++;
                    continue;
                }

                var title = Input.Truncated(Text(suggested, "titulo"), 120);
                var kind = Text(suggested, "tipo");
                if (title.Length == 0 || created >= NewSuggestionsPerRun || !SuggestionKind.All.Contains(kind))
                {
                    continue;
                }

                db.KnowledgeSuggestions.Add(new KnowledgeSuggestion
                {
                    Id = Guid.NewGuid(),
                    Kind = kind,
                    Title = title,
                    Detail = Limited(Text(suggested, "detalhe"), 4000),
                    ProposedContent = Limited(Text(suggested, "conteudo_sugerido"), 20_000),
                    Bank = Input.Truncated(Text(suggested, "banco"), 80),
                    Source = "analysis",
                    DeviceId = deviceId,
                    AttendantName = device.Label,
                    Question = Limited(Text(suggested, "pergunta_exemplo"), 2000),
                    Occurrences = occurrences,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                created++;
            }

            var actor = admin is null
                ? new AuditInfo(ActorType.System, null, "Sistema", "assistant.analysis",
                    $"Sistema analisou {batch.Count} falas de {device.Label} com a assistente", "device", deviceId)
                : AuditInfo.By(admin, "assistant.analysis",
                    $"{admin.Name} mandou analisar {batch.Count} falas de {device.Label} com a assistente", "device", deviceId);
            await audit.SaveAsync(db, actor, cancellationToken: ct);

            return new AnalysisResult(batch.Count, created, updated, more);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static string Limited(string value, int max)
    {
        var clean = AssistantEndpoints.Multiline(value);
        return clean.Length <= max ? clean : clean[..max];
    }
}

/// <summary>
/// Análise automática: de tempos em tempos, lê as conversas novas de cada atendente. Para não gastar à toa,
/// só roda quando há falas novas suficientes, a conversa já esfriou e a última análise tem quase um dia.
/// </summary>
public sealed class AnalysisWorker(IServiceProvider services, ILogger<AnalysisWorker> logger, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan BetweenAnalyses = TimeSpan.FromHours(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstWait, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is AssistantFailure or ApiError or DbUpdateException)
                {
                    logger.LogWarning(ex, "A análise automática das conversas parou nesta rodada: {Message}", ex.Message);
                }

                await Task.Delay(Every, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Servidor encerrando.
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var off = await db.TextSettings.AsNoTracking().AnyAsync(s => s.Key == TextSetting.AssistantAutoAnalysis && s.Value == "0", ct);
        if (off || await AssistantSecrets.ReadKeyAsync(db, configuration, ct) is null)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var pending = await AssistantAnalysis.PendingAsync(db, ct);
        var lastAt = await db.AssistantMessages.AsNoTracking()
            .GroupBy(m => m.DeviceId).Select(g => new { DeviceId = g.Key, At = g.Max(m => m.At) })
            .ToDictionaryAsync(g => g.DeviceId, g => g.At, ct);
        var analyzedAt = await db.AttendantInsights.AsNoTracking().ToDictionaryAsync(i => i.DeviceId, i => i.UpdatedAt, ct);
        var approved = (await db.Devices.AsNoTracking().Where(d => d.Approved).Select(d => d.Id).ToListAsync(ct)).ToHashSet();

        foreach (var (deviceId, count) in pending)
        {
            if (count < AssistantAnalysis.MinNewMessages || !approved.Contains(deviceId)
                || now - lastAt.GetValueOrDefault(deviceId, now) < Quiet
                || (analyzedAt.TryGetValue(deviceId, out var at) && now - at < BetweenAnalyses))
            {
                continue;
            }

            // Um lote por atendente por rodada; uma falha da IA encerra a rodada (não adianta insistir nas outras).
            await AssistantAnalysis.RunAsync(
                db, scope.ServiceProvider.GetRequiredService<IAssistantModel>(), configuration,
                scope.ServiceProvider.GetRequiredService<AuditWriter>(), time, deviceId, null, ct);
        }
    }
}
