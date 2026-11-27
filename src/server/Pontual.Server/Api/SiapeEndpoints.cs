using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Valor liberado do SIAPE na Central Neural (ver <see cref="SiapeMargin"/>): a ligação com o SQL da base, como escolher
/// os clientes, a rotina mensal e o que a IA já escreveu. As mensagens esperam aprovação em Mensagens para aprovar.
/// </summary>
public static class SiapeEndpoints
{
    private static readonly Permission Access = Permission.ConfigEdit;

    private sealed record SettingsBody(string? Server, string? Database, string? User, string? Password, string? Table,
        decimal? MinLiberado, int? Max, int? Day, string? BotId, string? Attendant, bool? Enabled, bool? AutoSend);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/neural/siape", OverviewAsync).Panel(Access);
        api.MapPut("/neural/siape/settings", SaveSettingsAsync).Panel(Access);
        api.MapPost("/neural/siape/test", TestAsync).Panel(Access);
        api.MapPost("/neural/siape/scan", ScanAsync).Panel(Access);
    }

    private static async Task<IResult> OverviewAsync(PontualDb db, ISiapeSource source, CrmClient crm, TimeProvider time, CancellationToken ct)
    {
        var values = await db.TextSettings.AsNoTracking().Where(s => s.Key.StartsWith("siape.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var settings = SiapeMargin.Settings.From(values);
        var access = SiapeMargin.Access(values);

        // As tabelas do banco e a escolha automática, para a tela mostrar o que a busca vai ler.
        object? tables = null;
        string sqlError = "", chosen = "";
        if (access is not null)
        {
            try
            {
                var (server, list) = await source.ProbeAsync(access, ct);
                var (current, why) = SiapeMargin.Choose(list, settings.Table);
                chosen = why;
                tables = new
                {
                    server,
                    list = list.Select(t => new
                    {
                        name = t.Name, createdAt = t.CreatedAt, hasPhone = t.HasPhone,
                        month = t.MonthKey is { } k ? SiapeMargin.MonthName(k) : "",
                        current = t == current,
                    }),
                };
            }
            catch (SiapeFailure failure)
            {
                sqlError = failure.Message;
            }
        }

        // As linhas de WhatsApp do CRM, para escolher por onde a mensagem sai.
        object lines = Array.Empty<object>();
        var crmError = "";
        if (await CrmClient.AccessAsync(db, ct) is { } crmAccess)
        {
            try
            {
                lines = (await crm.LinesAsync(crmAccess, ct)).Select(l => new { id = l.Id, funnel = l.Funnel }).ToList();
            }
            catch (CrmFailure failure)
            {
                crmError = failure.Message;
            }
        }
        else
        {
            crmError = "A ligação com o CRM não está configurada (aba Clientes do CRM).";
        }

        var now = time.GetUtcNow().UtcDateTime;
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(new DateTime(TimeZoneInfo.ConvertTimeFromUtc(now, AssistantPrompt.BrazilTime).Year,
            TimeZoneInfo.ConvertTimeFromUtc(now, AssistantPrompt.BrazilTime).Month, 1), DateTimeKind.Unspecified), AssistantPrompt.BrazilTime);
        var all = await db.CrmReplies.AsNoTracking().Where(r => r.Kind == SiapeMargin.Kind).Select(r => new { r.Status, r.CreatedAt }).ToListAsync(ct);
        var month = all.Where(r => r.CreatedAt >= monthStart).ToList();
        var recent = await db.CrmReplies.AsNoTracking().Where(r => r.Kind == SiapeMargin.Kind).OrderByDescending(r => r.CreatedAt).Take(60).ToListAsync(ct);
        return Results.Ok(new
        {
            server = values.GetValueOrDefault(TextSetting.SiapeServer, ""),
            database = values.GetValueOrDefault(TextSetting.SiapeDatabase, ""),
            user = values.GetValueOrDefault(TextSetting.SiapeUser, ""),
            hasPassword = values.GetValueOrDefault(TextSetting.SiapePassword, "").Length > 0,
            configured = access is not null,
            table = settings.Table,
            minLiberado = settings.MinLiberado, max = settings.Max, day = settings.Day,
            botId = settings.BotId, attendant = settings.Attendant, enabled = settings.Enabled, autoSend = settings.AutoSend,
            lastRun = values.GetValueOrDefault(TextSetting.SiapeLastRun, ""),
            lastMonth = values.GetValueOrDefault(TextSetting.SiapeLastMonth, ""),
            tables, sqlError, chosen, lines, crmError,
            month = new
            {
                created = month.Count, pending = month.Count(r => r.Status is CrmReplyStatus.Pending or CrmReplyStatus.Failed),
                sent = month.Count(r => r.Status == CrmReplyStatus.Sent), rejected = month.Count(r => r.Status == CrmReplyStatus.Rejected),
            },
            total = new { created = all.Count, sent = all.Count(r => r.Status == CrmReplyStatus.Sent) },
            recent = recent.Select(CrmEndpoints.Dto),
        });
    }

    private static async Task<IResult> SaveSettingsAsync(HttpContext context, PontualDb db, ISiapeSource source, CrmClient crm, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<SettingsBody>();
        var server = Input.Truncated(body.Server, 120).Trim();
        if (server.Length > 0 && server.Any(c => c is ';' or '=' or '\'' or '"'))
        {
            throw ApiError.BadRequest("O servidor é só o endereço, no formato IP,porta (ex.: 192.168.0.160,1433) ou NOME\\INSTANCIA.");
        }

        var day = body.Day ?? 1;
        if (day is < 1 or > 28)
        {
            throw ApiError.BadRequest("O dia do mês precisa estar entre 1 e 28.");
        }

        var max = body.Max ?? SiapeMargin.DefaultMax;
        if (max is < 1 or > SiapeMargin.MaxPerRun)
        {
            throw ApiError.BadRequest($"O máximo de mensagens por mês precisa estar entre 1 e {SiapeMargin.MaxPerRun}.");
        }

        if (body.MinLiberado is < 0)
        {
            throw ApiError.BadRequest("O valor liberado mínimo não pode ser negativo.");
        }

        await SetAsync(db, TextSetting.SiapeServer, server, ct);
        await SetAsync(db, TextSetting.SiapeDatabase, Input.Truncated(body.Database, 80).Trim(), ct);
        await SetAsync(db, TextSetting.SiapeUser, Input.Truncated(body.User, 80).Trim(), ct);
        if (!string.IsNullOrEmpty(body.Password))
        {
            await SetAsync(db, TextSetting.SiapePassword, AssistantSecrets.Protect(body.Password.Trim()), ct);
        }

        await SetAsync(db, TextSetting.SiapeTable, Input.Truncated(body.Table, 128).Trim(), ct);
        await SetAsync(db, TextSetting.SiapeMinLiberado, (body.MinLiberado ?? SiapeMargin.DefaultMinLiberado).ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, TextSetting.SiapeMax, max.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, TextSetting.SiapeDay, day.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, TextSetting.SiapeBotId, Input.Truncated(body.BotId, 80).Trim(), ct);
        await SetAsync(db, TextSetting.SiapeAttendant, Input.Truncated(body.Attendant, 80).Trim(), ct);
        var enabled = body.Enabled == true;
        var autoSend = body.AutoSend == true;
        await SetAsync(db, TextSetting.SiapeEnabled, enabled ? "1" : "0", ct);
        await SetAsync(db, TextSetting.SiapeAutoSend, autoSend ? "1" : "0", ct);
        await audit.SaveAsync(db, AuditInfo.By(admin, "siape.settings",
            $"{admin.Name} salvou o valor liberado do SIAPE ({(enabled ? "busca mensal ligada" : "busca mensal desligada")}, {(autoSend ? "envio direto pelo CRM" : "com aprovação")}, até {max} por mês)"), cancellationToken: ct);
        return await OverviewAsync(db, source, crm, time, ct);
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

    private static async Task<IResult> TestAsync(PontualDb db, ISiapeSource source, CancellationToken ct)
    {
        var access = await SiapeMargin.AccessAsync(db, ct) ?? throw ApiError.BadRequest("Preencha o servidor, o banco, o usuário e a senha e salve antes de testar.");
        try
        {
            var (server, tables) = await source.ProbeAsync(access, ct);
            var values = await db.TextSettings.AsNoTracking().Where(s => s.Key.StartsWith("siape.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            var settings = SiapeMargin.Settings.From(values);
            var (_, why) = SiapeMargin.Choose(tables, settings.Table);
            return Results.Ok(new { ok = true, message = $"Ligação com o SQL funcionando ({server}, {tables.Count} tabela(s)). {why}" });
        }
        catch (SiapeFailure failure)
        {
            throw ApiError.Conflict(failure.Message);
        }
    }

    private static async Task<IResult> ScanAsync(HttpContext context, PontualDb db, IServiceScopeFactory scopes, TimeProvider time, AuditWriter audit, ILogger<SiapeMarginWorker> logger, CancellationToken ct)
    {
        var admin = context.Admin();
        var result = await SiapeMarginWorker.RunAsync(scopes, time, logger, force: true, ct);
        await audit.SaveAsync(db, AuditInfo.By(admin, "siape.scan",
            $"{admin.Name} rodou a busca de valor liberado do SIAPE: {result.Checked} servidor(es) lido(s), {result.Created} mensagem(ns)"), cancellationToken: ct);
        return Results.Ok(new { result.Checked, result.Created, result.Sent, message = result.Note });
    }
}
