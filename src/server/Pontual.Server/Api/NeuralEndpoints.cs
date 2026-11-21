using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Central Neural da IA: a base de conhecimento da assistente (roteiros, documentos e conhecimentos), as
/// ligações entre eles, as sugestões vindas das atendentes e o perfil de atendimento de cada uma.
/// Todas as rotas exigem <see cref="Permission.ConfigEdit"/>, que só administrador e superadministrador têm.
/// </summary>
public static class NeuralEndpoints
{
    private static readonly Permission Access = Permission.ConfigEdit;

    private const int JsonBody = 4 * 1024 * 1024;
    private const int MaxTitle = 120;
    private const int MaxSuggestionsListed = 300;

    private sealed record ItemBody(
        string? Kind, string? Title, string? Description, string? Category, string? Bank, string? Keywords,
        string? Content, string? AiInstructions, string? Notes, bool? Enabled);
    private sealed record LinkBody(Guid? From, Guid? To);
    private sealed record SuggestionBody(string? Title, string? Detail, string? ProposedContent, string? Bank, string? Category);
    private sealed record ApproveBody(string? CreateKind, Guid? ItemId, string? Title, string? Content, string? Bank, string? Category, string? Description);
    private sealed record SettingsBody(bool? AutoAnalysis, bool? WhatsAppLearning, string? Manual);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/neural", OverviewAsync).Panel(Access);
        api.MapPut("/neural/settings", SaveSettingsAsync).Panel(Access);
        api.MapGet("/neural/search", SearchAsync).Panel(Access);

        api.MapGet("/neural/items/{id:guid}", ItemAsync).Panel(Access);
        api.MapPost("/neural/items", CreateItemAsync).Panel(Access);
        api.MapPut("/neural/items/{id:guid}", UpdateItemAsync).Panel(Access);
        api.MapDelete("/neural/items/{id:guid}", DeleteItemAsync).Panel(Access);
        api.MapPost("/neural/documents", UploadAsync).Panel(Access);
        api.MapGet("/neural/items/{id:guid}/file", FileAsync).Panel(Access);

        api.MapPost("/neural/links", CreateLinkAsync).Panel(Access);
        api.MapDelete("/neural/links/{id:guid}", DeleteLinkAsync).Panel(Access);

        api.MapGet("/neural/suggestions", SuggestionsAsync).Panel(Access);
        api.MapPut("/neural/suggestions/{id:guid}", UpdateSuggestionAsync).Panel(Access);
        api.MapPost("/neural/suggestions/{id:guid}/approve", ApproveAsync).Panel(Access);
        api.MapPost("/neural/suggestions/{id:guid}/reject", (Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct) =>
            DecideAsync(id, SuggestionStatus.Rejected, context, db, audit, time, ct)).Panel(Access);
        api.MapPost("/neural/suggestions/{id:guid}/reopen", (Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct) =>
            DecideAsync(id, SuggestionStatus.Pending, context, db, audit, time, ct)).Panel(Access);

        api.MapGet("/neural/attendants/{deviceId:guid}", AttendantAsync).Panel(Access);
        api.MapPost("/neural/attendants/{deviceId:guid}/analyze", AnalyzeAsync).Panel(Access);
    }

    // ------------------------------------------------------------------ visão geral

    private static object Meta(AssistantScript s) => new
    {
        id = s.Id,
        kind = s.Kind,
        title = s.Title,
        description = s.Description,
        category = s.Category,
        bank = s.Bank,
        keywords = s.Keywords,
        enabled = s.Enabled,
        chars = s.Content.Length,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
        updatedBy = s.UpdatedByName,
        source = s.Source,
        fileName = s.FileName,
        fileSize = s.FileSize,
        fileStatus = s.FileStatus,
        fileError = s.FileError,
    };

    private static async Task<IResult> OverviewAsync(PontualDb db, IConfiguration configuration, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        // Só os campos de cadastro; o texto de cada conhecimento é buscado quando ele é aberto.
        var items = await db.AssistantScripts.AsNoTracking().OrderBy(s => s.Title)
            .Select(s => new
            {
                id = s.Id, kind = s.Kind, title = s.Title, description = s.Description, category = s.Category, bank = s.Bank,
                keywords = s.Keywords, enabled = s.Enabled, chars = s.Content.Length, createdAt = s.CreatedAt, updatedAt = s.UpdatedAt,
                updatedBy = s.UpdatedByName, source = s.Source, fileName = s.FileName, fileSize = s.FileSize,
                fileStatus = s.FileStatus, fileError = s.FileError, hasInstructions = s.AiInstructions != "",
            })
            .ToListAsync(ct);
        var links = await db.KnowledgeLinks.AsNoTracking().Select(l => new { id = l.Id, from = l.FromId, to = l.ToId }).ToListAsync(ct);

        var usage = await db.AssistantMessages.AsNoTracking()
            .Where(m => m.Role == AssistantRole.User)
            .GroupBy(m => m.DeviceId)
            .Select(g => new { DeviceId = g.Key, Questions = g.Count(), LastAt = g.Max(m => m.At) })
            .ToDictionaryAsync(g => g.DeviceId, ct);
        var insights = await db.AttendantInsights.AsNoTracking()
            .Select(i => new { i.DeviceId, i.UpdatedAt, i.MessagesAnalyzed, HasProfile = i.ProfileJson != "" })
            .ToDictionaryAsync(i => i.DeviceId, ct);
        var waiting = await AssistantAnalysis.PendingAsync(db, ct);
        var whatsapp = await AssistantAnalysis.WhatsAppPendingAsync(db, ct);
        var coverage = await db.AssistantProfiles.AsNoTracking()
            .Select(p => new { p.DeviceId, p.Coverage, p.CoverageSchedule, p.AbsentOn }).ToDictionaryAsync(p => p.DeviceId, ct);
        var coveragePending = await db.CoverageReplies.AsNoTracking().Where(r => r.Status == CoverageStatus.Pending)
            .GroupBy(r => r.DeviceId).Select(g => new { DeviceId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.DeviceId, g => g.Count, ct);
        var devices = await db.Devices.AsNoTracking().Where(d => d.Approved).OrderBy(d => d.AttendantName).ThenBy(d => d.Hostname).ToListAsync(ct);

        var byStatus = await db.KnowledgeSuggestions.AsNoTracking()
            .GroupBy(s => s.Status).Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, ct);
        var pending = await db.KnowledgeSuggestions.AsNoTracking()
            .Where(s => s.Status == SuggestionStatus.Pending)
            .OrderByDescending(s => s.Occurrences).ThenByDescending(s => s.UpdatedAt).Take(60)
            .Select(s => new { id = s.Id, kind = s.Kind, title = s.Title, bank = s.Bank, occurrences = s.Occurrences, source = s.Source, attendant = s.AttendantName, updatedAt = s.UpdatedAt })
            .ToListAsync(ct);

        var helpful = await db.AssistantFeedback.AsNoTracking().CountAsync(f => f.Helpful, ct);
        var notHelpful = await db.AssistantFeedback.AsNoTracking().CountAsync(f => !f.Helpful, ct);

        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var enabledChars = items.Where(i => i.enabled).Sum(i => (long)i.chars);

        return Results.Ok(new
        {
            items,
            links,
            attendants = devices.Select(d =>
            {
                usage.TryGetValue(d.Id, out var used);
                insights.TryGetValue(d.Id, out var insight);
                coverage.TryGetValue(d.Id, out var covered);
                return new
                {
                    deviceId = d.Id,
                    name = string.IsNullOrWhiteSpace(d.AttendantName) ? d.Hostname : d.AttendantName,
                    hostname = d.Hostname,
                    questions = used?.Questions ?? 0,
                    lastAt = used?.LastAt,
                    hasProfile = (insight?.HasProfile ?? false) || AssistantPrompt.ImportedProfile(settings, d.AttendantName).Length > 0,
                    analyzedAt = insight?.UpdatedAt,
                    messagesAnalyzed = insight?.MessagesAnalyzed ?? 0,
                    newMessages = waiting.GetValueOrDefault(d.Id),
                    whatsappPending = whatsapp.GetValueOrDefault(d.Id),
                    coverage = covered?.Coverage ?? "",
                    absent = covered?.AbsentOn == Coverage.Day(now),
                    // Dias e horários em que a IA pode atender por ela, e se agora está dentro deles.
                    schedule = CoverageEndpoints.ScheduleDto(covered?.CoverageSchedule),
                    coverageNow = Coverage.WithinHours(now, covered?.CoverageSchedule),
                    coveragePending = coveragePending.GetValueOrDefault(d.Id),
                };
            }),
            suggestions = new
            {
                pending = byStatus.GetValueOrDefault(SuggestionStatus.Pending),
                approved = byStatus.GetValueOrDefault(SuggestionStatus.Approved),
                rejected = byStatus.GetValueOrDefault(SuggestionStatus.Rejected),
            },
            pendingSuggestions = pending,
            feedback = new { helpful, notHelpful },
            brain = new
            {
                // "inline": a IA lê a base inteira a cada pergunta. "index": lê o índice e abre o que precisa.
                mode = AssistantEndpoints.FitsInline(enabledChars) ? "inline" : "index",
                enabledChars,
                inlineBudget = KnowledgeLimits.InlineBudget,
                limitChars = KnowledgeLimits.All,
                limitItems = KnowledgeLimits.Items,
            },
            settings = new
            {
                hasKey = await AssistantSecrets.ReadKeyAsync(db, configuration, ct) is not null,
                model = AssistantEndpoints.ModelOf(settings),
                autoAnalysis = settings.GetValueOrDefault(TextSetting.AssistantAutoAnalysis, "1") != "0",
                whatsappLearning = settings.GetValueOrDefault(TextSetting.AssistantWhatsAppLearning, "1") != "0",
                generalInstructions = settings.GetValueOrDefault(TextSetting.AssistantInstructions, "").Length,
                // O manual de comportamento e comunicação: o mesmo texto das orientações gerais da Central do TI.
                manual = settings.GetValueOrDefault(TextSetting.AssistantInstructions, ""),
            },
            limits = new { item = KnowledgeLimits.Item, upload = KnowledgeLimits.Upload, extensions = DocumentText.Extensions, title = MaxTitle, manual = AssistantEndpoints.MaxGeneralInstructions },
        });
    }

    private static async Task<IResult> SaveSettingsAsync(HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<SettingsBody>();
        if (body.AutoAnalysis is null && body.WhatsAppLearning is null && body.Manual is null)
        {
            throw ApiError.BadRequest("Informe o que deve ser alterado.");
        }

        var changes = new List<string>();
        async Task SetAsync(string key, bool on, string what)
        {
            var setting = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (setting is null)
            {
                setting = new TextSetting { Key = key };
                db.TextSettings.Add(setting);
            }

            setting.Value = on ? "1" : "0";
            changes.Add($"{(on ? "ligou" : "desligou")} {what}");
        }

        if (body.AutoAnalysis is { } auto)
        {
            await SetAsync(TextSetting.AssistantAutoAnalysis, auto, "a análise automática das conversas");
        }

        if (body.WhatsAppLearning is { } learning)
        {
            await SetAsync(TextSetting.AssistantWhatsAppLearning, learning, "o registro das conversas do WhatsApp para a análise");
            if (!learning)
            {
                // Desligado: o que estava na fila e ainda não foi lido pela IA é apagado.
                db.WhatsAppCaptures.RemoveRange(await db.WhatsAppCaptures.ToListAsync(ct));
            }
        }

        if (body.Manual is not null)
        {
            // O manual de comunicação é o mesmo texto das "orientações gerais" editadas em Central do TI → Assistente.
            var manual = AssistantEndpoints.Multiline(body.Manual);
            if (manual.Length > AssistantEndpoints.MaxGeneralInstructions)
            {
                throw ApiError.BadRequest($"O manual de comunicação aceita no máximo {AssistantEndpoints.MaxGeneralInstructions} caracteres.");
            }

            var setting = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == TextSetting.AssistantInstructions, ct);
            if (setting is null)
            {
                setting = new TextSetting { Key = TextSetting.AssistantInstructions };
                db.TextSettings.Add(setting);
            }

            setting.Value = manual;
            changes.Add(manual.Length > 0 ? "alterou o manual de comunicação da IA" : "apagou o manual de comunicação da IA");
        }

        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.settings", $"{admin.Name} {string.Join(" e ", changes)}"), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SearchAsync(string? q, PontualDb db, CancellationToken ct)
    {
        var query = Input.Truncated(q, 200);
        if (query.Length < 2)
        {
            return Results.Ok(new { items = Array.Empty<object>(), suggestions = Array.Empty<object>() });
        }

        var all = await db.AssistantScripts.AsNoTracking().ToListAsync(ct);
        var hits = KnowledgeText.Search(all, query, 40, 240);

        var terms = KnowledgeText.Terms(query);
        var suggestions = (await db.KnowledgeSuggestions.AsNoTracking().OrderByDescending(s => s.UpdatedAt).Take(500).ToListAsync(ct))
            .Select(s => new { Suggestion = s, Text = KnowledgeText.Fold($"{s.Title} {s.Bank} {s.Detail} {s.Question} {s.ProposedContent}") })
            .Select(s => new { s.Suggestion, Matched = terms.Count(t => s.Text.Contains(t, StringComparison.Ordinal)) })
            .Where(s => s.Matched > 0)
            .OrderByDescending(s => s.Matched).Take(15)
            .Select(s => new { id = s.Suggestion.Id, title = s.Suggestion.Title, kind = s.Suggestion.Kind, status = s.Suggestion.Status, snippet = Input.Truncated(s.Suggestion.Detail, 240) });

        return Results.Ok(new
        {
            items = hits.Select(h => new
            {
                id = h.Item.Id, kind = h.Item.Kind, title = h.Item.Title, bank = h.Item.Bank, category = h.Item.Category,
                enabled = h.Item.Enabled, part = h.Part, parts = h.Parts, snippet = h.Snippet,
            }),
            suggestions,
        });
    }

    // ------------------------------------------------------------------ conhecimentos

    private static async Task<IResult> ItemAsync(Guid id, PontualDb db, CancellationToken ct)
    {
        var item = await db.AssistantScripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiError.NotFound("Conhecimento não encontrado.");
        var links = await db.KnowledgeLinks.AsNoTracking().Where(l => l.FromId == id || l.ToId == id).ToListAsync(ct);
        var otherIds = links.Select(l => l.FromId == id ? l.ToId : l.FromId).ToList();
        var others = await db.AssistantScripts.AsNoTracking().Where(s => otherIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.Kind }).ToDictionaryAsync(s => s.Id, ct);

        return Results.Ok(new
        {
            item = Meta(item),
            content = item.Content,
            aiInstructions = item.AiInstructions,
            notes = item.Notes,
            parts = KnowledgeText.Parts(item.Content).Count,
            links = links
                .Select(l => new { Link = l, Other = others.GetValueOrDefault(l.FromId == id ? l.ToId : l.FromId) })
                .Where(l => l.Other is not null)
                .Select(l => new { linkId = l.Link.Id, id = l.Other!.Id, title = l.Other.Title, kind = l.Other.Kind }),
        });
    }

    private static string Optional(string? value, string what, int max, bool multiline = false)
    {
        var text = multiline ? AssistantEndpoints.Multiline(value) : Input.Clean(value);
        if (text.Length > max)
        {
            throw ApiError.BadRequest($"O campo de {what} aceita no máximo {max} caracteres.");
        }

        return text;
    }

    private static string ContentOf(string? raw)
    {
        var content = AssistantEndpoints.Multiline(raw);
        if (content.Length > KnowledgeLimits.Item)
        {
            throw ApiError.BadRequest($"O texto tem {content.Length:N0} caracteres; o máximo por conhecimento é {KnowledgeLimits.Item:N0}. Divida em partes.");
        }

        return content;
    }

    private static void Apply(AssistantScript item, ItemBody body)
    {
        if (body.Title is not null)
        {
            item.Title = Input.Required(body.Title, "o título", MaxTitle);
        }

        if (body.Description is not null)
        {
            item.Description = Optional(body.Description, "descrição", 500);
        }

        if (body.Category is not null)
        {
            item.Category = Optional(body.Category, "categoria", 60);
        }

        if (body.Bank is not null)
        {
            item.Bank = Optional(body.Bank, "banco ou assunto", 80);
        }

        if (body.Keywords is not null)
        {
            item.Keywords = Optional(body.Keywords, "palavras-chave", 300);
        }

        if (body.AiInstructions is not null)
        {
            item.AiInstructions = Optional(body.AiInstructions, "instruções para a IA", 2000, multiline: true);
        }

        if (body.Notes is not null)
        {
            item.Notes = Optional(body.Notes, "observações", 2000, multiline: true);
        }

        if (body.Content is not null)
        {
            item.Content = ContentOf(body.Content);
        }

        if (body.Enabled is { } enabled)
        {
            item.Enabled = enabled;
        }

        if (item.Content.Length == 0)
        {
            // Sem texto não há o que a IA ler: um PDF escaneado fica guardado e desligado.
            if (item.Kind != KnowledgeKind.Document)
            {
                throw ApiError.BadRequest("Escreva o texto do conhecimento.");
            }

            if (body.Enabled == true)
            {
                throw ApiError.Conflict("Este documento ainda não tem texto para a IA ler, por isso não pode ser ligado.");
            }

            item.Enabled = false;
        }
        else if (item.FileStatus == FileStatus.NoText)
        {
            // O texto foi escrito à mão sobre um PDF escaneado: passa a valer como processado.
            item.FileStatus = FileStatus.Processed;
            item.FileError = "";
        }
    }

    private static async Task<IResult> CreateItemAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        Grow(context, JsonBody);
        var body = await context.ReadBodyAsync<ItemBody>();
        var kind = body.Kind is KnowledgeKind.Script or KnowledgeKind.Knowledge
            ? body.Kind
            : throw ApiError.BadRequest("Tipo inválido. Roteiros e conhecimentos são escritos aqui; documentos entram pelo envio de arquivo.");
        await EnsureCountAsync(db, ct);

        var now = time.GetUtcNow().UtcDateTime;
        var item = new AssistantScript { Id = Guid.NewGuid(), Kind = kind, Title = "", Content = "", CreatedAt = now, UpdatedAt = now, UpdatedByName = admin.Name, Source = "manual" };
        Apply(item, body with { Title = body.Title ?? "" });
        await AssistantEndpoints.EnsureRoomAsync(db, item, ct);
        db.AssistantScripts.Add(item);

        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.item.create",
            $"{admin.Name} cadastrou o {KnowledgeKind.Label(kind)} \"{item.Title}\" na base da assistente", "assistant-script", item.Id), cancellationToken: ct);
        return Results.Ok(Meta(item));
    }

    private static async Task<IResult> UpdateItemAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        Grow(context, JsonBody);
        var body = await context.ReadBodyAsync<ItemBody>();
        var item = await db.AssistantScripts.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiError.NotFound("Conhecimento não encontrado.");

        if (body.Kind is not null && body.Kind != item.Kind)
        {
            // Um documento continua documento (tem arquivo); roteiro e conhecimento podem trocar entre si.
            if (item.Kind == KnowledgeKind.Document || body.Kind is not (KnowledgeKind.Script or KnowledgeKind.Knowledge))
            {
                throw ApiError.BadRequest("Este conhecimento não pode mudar para esse tipo.");
            }

            item.Kind = body.Kind;
        }

        Apply(item, body);
        await AssistantEndpoints.EnsureRoomAsync(db, item, ct);
        item.UpdatedAt = time.GetUtcNow().UtcDateTime;
        item.UpdatedByName = admin.Name;

        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.item.update",
            $"{admin.Name} alterou o {KnowledgeKind.Label(item.Kind)} \"{item.Title}\" da base da assistente{(item.Enabled ? "" : " (desligado)")}", "assistant-script", item.Id), cancellationToken: ct);
        return Results.Ok(Meta(item));
    }

    private static async Task<IResult> DeleteItemAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var item = await db.AssistantScripts.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiError.NotFound("Conhecimento não encontrado.");
        db.AssistantScripts.Remove(item);
        db.KnowledgeLinks.RemoveRange(await db.KnowledgeLinks.Where(l => l.FromId == id || l.ToId == id).ToListAsync(ct));
        foreach (var suggestion in await db.KnowledgeSuggestions.Where(s => s.ItemId == id).ToListAsync(ct))
        {
            suggestion.ItemId = null;
        }

        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.item.delete",
            $"{admin.Name} removeu o {KnowledgeKind.Label(item.Kind)} \"{item.Title}\" da base da assistente", "assistant-script", item.Id), cancellationToken: ct);
        DeleteFiles(db, id);
        return Results.NoContent();
    }

    private static async Task EnsureCountAsync(PontualDb db, CancellationToken ct)
    {
        if (await db.AssistantScripts.CountAsync(ct) >= KnowledgeLimits.Items)
        {
            throw ApiError.Conflict($"O limite é de {KnowledgeLimits.Items} conhecimentos. Junte ou remova algum antes de criar outro.");
        }
    }

    // ------------------------------------------------------------------ documentos

    /// <summary>As rotas comuns aceitam corpo pequeno; as desta Central recebem textos longos e arquivos.</summary>
    private static void Grow(HttpContext context, long bytes)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = bytes;
        }
    }

    /// <summary>Pasta dos arquivos originais dos documentos, ao lado do banco de dados.</summary>
    private static string FilesFolder(PontualDb db)
    {
        var source = Path.GetFullPath(db.Database.GetDbConnection().DataSource);
        return Path.Combine(Path.GetDirectoryName(source) ?? AppContext.BaseDirectory, "documentos");
    }

    private static string? FileOf(PontualDb db, Guid id)
    {
        var folder = FilesFolder(db);
        return DocumentText.Extensions.Select(ext => Path.Combine(folder, id.ToString("N") + ext)).FirstOrDefault(File.Exists);
    }

    private static void DeleteFiles(PontualDb db, Guid id)
    {
        var folder = FilesFolder(db);
        foreach (var extension in DocumentText.Extensions)
        {
            try
            {
                File.Delete(Path.Combine(folder, id.ToString("N") + extension));
            }
            catch (IOException)
            {
                // Arquivo em uso: sobra um arquivo sem dono na pasta, sem efeito na base.
            }
        }
    }

    /// <summary>
    /// Recebe o arquivo no corpo da requisição (nome em <c>X-File-Name</c>, codificado como em uma URL).
    /// Com <c>?replace=id</c>, troca o arquivo de um documento que já existe. O texto é extraído na hora:
    /// se a extração falhar, nada é gravado e o erro volta para a tela.
    /// </summary>
    private static async Task<IResult> UploadAsync(
        Guid? replace, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var admin = context.Admin();
        Grow(context, KnowledgeLimits.Upload + 64 * 1024);

        string name;
        try
        {
            name = Path.GetFileName(Input.Truncated(Uri.UnescapeDataString(context.Request.Headers["X-File-Name"].ToString()), 150));
        }
        catch (UriFormatException)
        {
            name = "";
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (name.Length == 0 || !DocumentText.Extensions.Contains(extension))
        {
            throw ApiError.BadRequest("Formato não aceito. Envie um arquivo PDF, DOCX ou TXT.");
        }

        var megabytes = KnowledgeLimits.Upload / (1024 * 1024);
        if (context.Request.ContentLength > KnowledgeLimits.Upload)
        {
            throw ApiError.BadRequest($"O arquivo passa de {megabytes} MB.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > KnowledgeLimits.Upload)
            {
                throw ApiError.BadRequest($"O arquivo passa de {megabytes} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw ApiError.BadRequest("O arquivo está vazio.");
        }

        var extracted = DocumentText.Extract(name, bytes, loggers.CreateLogger("Pontual.Documentos"));
        if (extracted.Status == FileStatus.Error)
        {
            throw ApiError.BadRequest(extracted.Error);
        }

        var now = time.GetUtcNow().UtcDateTime;
        AssistantScript item;
        if (replace is { } existingId)
        {
            item = await db.AssistantScripts.FirstOrDefaultAsync(s => s.Id == existingId && s.Kind == KnowledgeKind.Document, ct)
                ?? throw ApiError.NotFound("Documento não encontrado.");
        }
        else
        {
            await EnsureCountAsync(db, ct);
            item = new AssistantScript
            {
                Id = Guid.NewGuid(),
                Kind = KnowledgeKind.Document,
                Title = Input.Truncated(Path.GetFileNameWithoutExtension(name), MaxTitle),
                Content = "",
                CreatedAt = now,
                Source = "arquivo",
                Enabled = true,
            };
            if (item.Title.Length == 0)
            {
                item.Title = "Documento";
            }

            db.AssistantScripts.Add(item);
        }

        item.Content = extracted.Text;
        item.FileName = name;
        item.FileSize = bytes.Length;
        item.FileStatus = extracted.Status;
        item.FileError = extracted.Error;
        item.Enabled = extracted.Status == FileStatus.Processed && item.Enabled;
        item.UpdatedAt = now;
        item.UpdatedByName = admin.Name;
        await AssistantEndpoints.EnsureRoomAsync(db, item, ct);

        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.document.upload",
            $"{admin.Name} {(replace is null ? "enviou" : "substituiu")} o documento \"{item.Title}\" da base da assistente", "assistant-script", item.Id), cancellationToken: ct);

        DeleteFiles(db, item.Id);
        Directory.CreateDirectory(FilesFolder(db));
        await File.WriteAllBytesAsync(Path.Combine(FilesFolder(db), item.Id.ToString("N") + extension), bytes, ct);

        return Results.Ok(Meta(item));
    }

    private static async Task<IResult> FileAsync(Guid id, PontualDb db, CancellationToken ct)
    {
        var item = await db.AssistantScripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.Kind == KnowledgeKind.Document, ct)
            ?? throw ApiError.NotFound("Documento não encontrado.");
        var path = FileOf(db, id) ?? throw ApiError.NotFound("O arquivo original deste documento não está mais guardado.");
        return Results.File(path, "application/octet-stream", item.FileName);
    }

    // ------------------------------------------------------------------ ligações

    private static async Task<IResult> CreateLinkAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<LinkBody>();
        if (body.From is not { } from || body.To is not { } to || from == to)
        {
            throw ApiError.BadRequest("Escolha dois conhecimentos diferentes para ligar.");
        }

        var both = await db.AssistantScripts.AsNoTracking().Where(s => s.Id == from || s.Id == to).Select(s => new { s.Id, s.Title }).ToListAsync(ct);
        if (both.Count != 2)
        {
            throw ApiError.NotFound("Conhecimento não encontrado.");
        }

        // Sem direção: a ligação A–B é a mesma que B–A.
        var (a, b) = from.CompareTo(to) < 0 ? (from, to) : (to, from);
        if (await db.KnowledgeLinks.AnyAsync(l => l.FromId == a && l.ToId == b, ct))
        {
            throw ApiError.Conflict("Estes dois conhecimentos já estão ligados.");
        }

        var link = new KnowledgeLink { Id = Guid.NewGuid(), FromId = a, ToId = b, CreatedAt = time.GetUtcNow().UtcDateTime };
        db.KnowledgeLinks.Add(link);
        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.link.create",
            $"{admin.Name} ligou \"{both[0].Title}\" a \"{both[1].Title}\" na base da assistente"), cancellationToken: ct);
        return Results.Ok(new { id = link.Id, from = a, to = b });
    }

    private static async Task<IResult> DeleteLinkAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var link = await db.KnowledgeLinks.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiError.NotFound("Ligação não encontrada.");
        db.KnowledgeLinks.Remove(link);
        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.link.delete", $"{admin.Name} desfez uma ligação entre conhecimentos da assistente"), cancellationToken: ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ sugestões

    private static object SuggestionDto(KnowledgeSuggestion s) => new
    {
        id = s.Id,
        kind = s.Kind,
        title = s.Title,
        detail = s.Detail,
        proposedContent = s.ProposedContent,
        bank = s.Bank,
        category = s.Category,
        source = s.Source,
        deviceId = s.DeviceId,
        attendant = s.AttendantName,
        question = s.Question,
        answer = s.Answer,
        occurrences = s.Occurrences,
        status = s.Status,
        itemId = s.ItemId,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
        reviewedAt = s.ReviewedAt,
        reviewedBy = s.ReviewedByName,
    };

    private static async Task<IResult> SuggestionsAsync(string? status, PontualDb db, CancellationToken ct)
    {
        var query = db.KnowledgeSuggestions.AsNoTracking();
        if (status is SuggestionStatus.Pending or SuggestionStatus.Approved or SuggestionStatus.Rejected)
        {
            query = query.Where(s => s.Status == status);
        }

        var list = await query.OrderByDescending(s => s.UpdatedAt).Take(MaxSuggestionsListed).ToListAsync(ct);
        return Results.Ok(list.Select(SuggestionDto));
    }

    private static async Task<KnowledgeSuggestion> PendingAsync(PontualDb db, Guid id, CancellationToken ct)
    {
        var suggestion = await db.KnowledgeSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiError.NotFound("Sugestão não encontrada.");
        return suggestion.Status == SuggestionStatus.Pending
            ? suggestion
            : throw ApiError.Conflict("Esta sugestão já foi revisada. Reabra-a para mexer de novo.");
    }

    private static async Task<IResult> UpdateSuggestionAsync(Guid id, HttpContext context, PontualDb db, TimeProvider time, CancellationToken ct)
    {
        Grow(context, JsonBody);
        var body = await context.ReadBodyAsync<SuggestionBody>();
        var suggestion = await PendingAsync(db, id, ct);

        if (body.Title is not null)
        {
            suggestion.Title = Input.Required(body.Title, "o título", MaxTitle);
        }

        if (body.Detail is not null)
        {
            suggestion.Detail = Optional(body.Detail, "detalhe", 4000, multiline: true);
        }

        if (body.ProposedContent is not null)
        {
            suggestion.ProposedContent = ContentOf(body.ProposedContent);
        }

        if (body.Bank is not null)
        {
            suggestion.Bank = Optional(body.Bank, "banco ou assunto", 80);
        }

        if (body.Category is not null)
        {
            suggestion.Category = Optional(body.Category, "categoria", 60);
        }

        suggestion.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return Results.Ok(SuggestionDto(suggestion));
    }

    /// <summary>
    /// Aprovar é o único caminho de uma sugestão até a IA. Ou nasce um conhecimento novo (roteiro ou
    /// conhecimento operacional), ou o texto aprovado é acrescentado a um conhecimento que já existe.
    /// </summary>
    private static async Task<IResult> ApproveAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        Grow(context, JsonBody);
        var body = await context.ReadBodyAsync<ApproveBody>();
        var suggestion = await PendingAsync(db, id, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var content = ContentOf(body.Content);

        AssistantScript item;
        string done;
        if (body.ItemId is { } itemId)
        {
            item = await db.AssistantScripts.FirstOrDefaultAsync(s => s.Id == itemId, ct) ?? throw ApiError.NotFound("Conhecimento não encontrado.");
            if (content.Length > 0)
            {
                var joined = item.Content.Length == 0 ? content : item.Content + "\n\n" + content;
                if (joined.Length > KnowledgeLimits.Item)
                {
                    throw ApiError.BadRequest($"Com este acréscimo o conhecimento passaria de {KnowledgeLimits.Item:N0} caracteres.");
                }

                item.Content = joined;
                item.UpdatedAt = now;
                item.UpdatedByName = admin.Name;
                await AssistantEndpoints.EnsureRoomAsync(db, item, ct);
            }

            done = content.Length > 0 ? $"acrescentou a sugestão \"{suggestion.Title}\" a" : $"vinculou a sugestão \"{suggestion.Title}\" a";
        }
        else
        {
            var kind = body.CreateKind is KnowledgeKind.Script or KnowledgeKind.Knowledge
                ? body.CreateKind
                : throw ApiError.BadRequest("Escolha transformar em roteiro, em conhecimento, ou vincular a um conhecimento existente.");
            if (content.Length == 0)
            {
                throw ApiError.BadRequest("Escreva o texto que a assistente vai passar a usar.");
            }

            await EnsureCountAsync(db, ct);
            item = new AssistantScript
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Title = Input.Required(body.Title ?? suggestion.Title, "o título", MaxTitle),
                Content = content,
                Description = Optional(body.Description, "descrição", 500),
                Bank = Optional(body.Bank ?? suggestion.Bank, "banco ou assunto", 80),
                Category = Optional(body.Category ?? suggestion.Category, "categoria", 60),
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedByName = admin.Name,
                Source = "atendentes",
            };
            await AssistantEndpoints.EnsureRoomAsync(db, item, ct);
            db.AssistantScripts.Add(item);
            done = $"aprovou a sugestão \"{suggestion.Title}\" e criou o {KnowledgeKind.Label(kind)}";
        }

        suggestion.Status = SuggestionStatus.Approved;
        suggestion.ItemId = item.Id;
        suggestion.ReviewedAt = now;
        suggestion.ReviewedByName = admin.Name;
        suggestion.UpdatedAt = now;

        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.suggestion.approve",
            $"{admin.Name} {done} \"{item.Title}\" na base da assistente", "assistant-script", item.Id), cancellationToken: ct);
        return Results.Ok(new { suggestion = SuggestionDto(suggestion), item = Meta(item) });
    }

    private static async Task<IResult> DecideAsync(Guid id, string status, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var suggestion = await db.KnowledgeSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiError.NotFound("Sugestão não encontrada.");
        if (suggestion.Status == status)
        {
            return Results.Ok(SuggestionDto(suggestion));
        }

        if (status == SuggestionStatus.Rejected && suggestion.Status != SuggestionStatus.Pending)
        {
            throw ApiError.Conflict("Esta sugestão já foi revisada.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        suggestion.Status = status;
        suggestion.UpdatedAt = now;
        suggestion.ReviewedAt = status == SuggestionStatus.Pending ? null : now;
        suggestion.ReviewedByName = status == SuggestionStatus.Pending ? "" : admin.Name;
        // Reabrir não desfaz o conhecimento que nasceu da aprovação; só devolve a sugestão à fila.

        await audit.SaveAsync(db, AuditInfo.By(admin, "neural.suggestion." + (status == SuggestionStatus.Rejected ? "reject" : "reopen"),
            $"{admin.Name} {(status == SuggestionStatus.Rejected ? "rejeitou" : "reabriu")} a sugestão \"{suggestion.Title}\" da assistente"), cancellationToken: ct);
        return Results.Ok(SuggestionDto(suggestion));
    }

    // ------------------------------------------------------------------ atendentes

    private static async Task<IResult> AttendantAsync(Guid deviceId, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        // O perfil é leitura de uso individual: vale a mesma regra das conversas, com registro de quem leu.
        context.Demand(Permission.UsageReadIndividual);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        var insight = await db.AttendantInsights.AsNoTracking().FirstOrDefaultAsync(i => i.DeviceId == deviceId, ct);
        var through = insight?.AnalyzedThroughMessageId ?? 0;

        static JsonElement? Parse(string json)
        {
            if (json.Length == 0)
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(json).RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var profile = Parse(insight?.ProfileJson ?? "");
        // Perfil tirado do histórico de atendimentos importado, achado pelo nome da atendente.
        var history = Parse(AssistantPrompt.ImportedProfile(
            await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct), device.AttendantName));

        var questions = await db.AssistantMessages.AsNoTracking().CountAsync(m => m.DeviceId == deviceId && m.Role == AssistantRole.User, ct);
        var newMessages = await db.AssistantMessages.AsNoTracking().CountAsync(m => m.DeviceId == deviceId && m.Id > through, ct);
        var whatsappPending = await db.WhatsAppCaptures.AsNoTracking().CountAsync(c => c.DeviceId == deviceId, ct);
        var helpful = await db.AssistantFeedback.AsNoTracking().CountAsync(f => f.DeviceId == deviceId && f.Helpful, ct);
        var notHelpful = await db.AssistantFeedback.AsNoTracking().CountAsync(f => f.DeviceId == deviceId && !f.Helpful, ct);
        var suggestions = await db.KnowledgeSuggestions.AsNoTracking()
            .Where(s => s.DeviceId == deviceId).OrderByDescending(s => s.UpdatedAt).Take(20)
            .Select(s => new { id = s.Id, title = s.Title, kind = s.Kind, status = s.Status })
            .ToListAsync(ct);
        var instructions = await db.AssistantProfiles.AsNoTracking().Where(p => p.DeviceId == deviceId).Select(p => p.Instructions).FirstOrDefaultAsync(ct) ?? "";

        if (profile is not null || history is not null)
        {
            await audit.SaveAsync(db, AuditInfo.By(admin, "neural.profile.read",
                $"{admin.Name} leu o perfil de atendimento de {device.Label}", "device", device.Id), cancellationToken: ct);
        }

        return Results.Ok(new
        {
            deviceId,
            name = string.IsNullOrWhiteSpace(device.AttendantName) ? device.Hostname : device.AttendantName,
            hostname = device.Hostname,
            profile,
            history,
            analyzedAt = insight?.UpdatedAt,
            messagesAnalyzed = insight?.MessagesAnalyzed ?? 0,
            model = insight?.Model ?? "",
            questions,
            newMessages,
            whatsappPending,
            whatsappAnalyzed = insight?.WhatsAppAnalyzed ?? 0,
            feedback = new { helpful, notHelpful },
            suggestions,
            instructions,
            coverage = await CoverageEndpoints.ListAsync(db, deviceId, time.GetUtcNow().UtcDateTime, ct),
        });
    }

    /// <summary>Análise pedida pelo administrador: lê as falas novas agora, em até três lotes.</summary>
    private static async Task<IResult> AnalyzeAsync(
        Guid deviceId, HttpContext context, PontualDb db, IAssistantModel model, IConfiguration configuration, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        context.Demand(Permission.UsageReadIndividual);

        int read = 0, conversations = 0, created = 0, updated = 0;
        var more = true;
        try
        {
            for (var batch = 0; batch < 3 && more; batch++)
            {
                var result = await AssistantAnalysis.RunAsync(db, model, configuration, audit, time, deviceId, admin, ct);
                read += result.MessagesRead;
                conversations += result.ConversationsRead;
                created += result.NewSuggestions;
                updated += result.UpdatedSuggestions;
                more = result.More;
            }
        }
        catch (AssistantFailure failure)
        {
            throw new ApiError(StatusCodes.Status502BadGateway, failure.Message);
        }

        return Results.Ok(new { messagesRead = read, conversationsRead = conversations, newSuggestions = created, updatedSuggestions = updated, more });
    }
}
