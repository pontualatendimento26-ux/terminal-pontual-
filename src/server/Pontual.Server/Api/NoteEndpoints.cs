using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// O texto de uma nota adesiva. Nunca é HTML: é uma lista de blocos (parágrafo ou item de lista), cada um com
/// trechos de texto e a formatação de cada trecho (negrito, itálico, sublinhado, riscado). Quem mostra a nota
/// (o Terminal e a Central) monta a tela a partir disto, então nada do que a atendente cola vira marcação.
/// Formato: <c>[{"t":"p"|"li","r":[{"s":"texto","f":0..15}]}]</c>, com <c>f</c> somando 1 negrito, 2 itálico,
/// 4 sublinhado e 8 riscado.
/// </summary>
public static class NoteContent
{
    public static readonly string[] Colors = ["yellow", "green", "pink", "purple", "blue", "gray", "charcoal"];

    public const int MaxBlocks = 400;
    public const int MaxRunsPerBlock = 60;
    public const int MaxChars = 20_000;

    private sealed record Run(string S, int F);
    private sealed record Block(string T, List<Run> R);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Color(string? value) => Array.IndexOf(Colors, value) >= 0 ? value! : Colors[0];

    /// <summary>Confere e reescreve o que o Terminal mandou. O que não tem a forma esperada é recusado.</summary>
    public static string Normalize(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            throw ApiError.BadRequest("O texto da nota não está no formato esperado.");
        }

        var blocks = new List<Block>();
        var total = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (blocks.Count >= MaxBlocks)
            {
                throw ApiError.BadRequest("A nota tem linhas demais.");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw ApiError.BadRequest("O texto da nota não está no formato esperado.");
            }

            var type = item.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "li" ? "li" : "p";
            var runs = new List<Run>();
            if (item.TryGetProperty("r", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                foreach (var run in r.EnumerateArray())
                {
                    if (run.ValueKind != JsonValueKind.Object
                        || !run.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.String)
                    {
                        throw ApiError.BadRequest("O texto da nota não está no formato esperado.");
                    }

                    var text = Text(s.GetString());
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    if (runs.Count >= MaxRunsPerBlock)
                    {
                        throw ApiError.BadRequest("A nota tem formatação demais numa linha só.");
                    }

                    total += text.Length;
                    if (total > MaxChars)
                    {
                        throw ApiError.BadRequest($"A nota aceita no máximo {MaxChars} caracteres.");
                    }

                    var format = run.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var bits) ? bits & 15 : 0;
                    runs.Add(new Run(text, format));
                }
            }

            blocks.Add(new Block(type, runs));
        }

        return JsonSerializer.Serialize(blocks, Options);
    }

    /// <summary>Recado digitado na Central: cada linha vira um parágrafo, sem formatação.</summary>
    public static string FromText(string text)
    {
        var blocks = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => new Block("p", Text(line) is { Length: > 0 } clean ? [new Run(clean, 0)] : []))
            .ToList();
        return JsonSerializer.Serialize(blocks, Options);
    }

    /// <summary>O que está guardado, pronto para ir no JSON da resposta (ou uma nota vazia, se estiver estragado).</summary>
    public static JsonElement Parse(string stored)
    {
        try
        {
            using var document = JsonDocument.Parse(stored);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("[]");
            return empty.RootElement.Clone();
        }
    }

    /// <summary>Tira caracteres de controle e surrogates soltos, mas preserva os espaços (inclusive nas pontas).</summary>
    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
            }
            else if (c == '\t' || c == ' ')
            {
                builder.Append(' ');
            }
            else if (!char.IsSurrogate(c) && !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Notas adesivas da Pontual (pedido do dono, 25/11/2026). O Terminal guarda as notas da atendente aqui, por computador;
/// a Central deixa o TI ler as notas e colar um recado na tela da atendente. Ler e enviar ficam na auditoria (sem o texto).
/// </summary>
public static class NoteEndpoints
{
    public const int MaxNotesPerDevice = 50;

    /// <summary>Por quanto tempo um recado retirado pelo TI continua marcado, para o Terminal tirar da tela.</summary>
    private static readonly TimeSpan RemovedKept = TimeSpan.FromDays(30);

    private sealed record NoteBody(string? Color, JsonElement Content, int X = 0, int Y = 0, int Width = 0, int Height = 0, bool Open = false);
    private sealed record SendBody(string? Text, string? Color);

    public static void Map(RouteGroupBuilder api)
    {
        // Terminal: as notas do próprio computador.
        api.MapGet("/device/notes", ListAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapPut("/device/notes/{noteId:guid}", SaveAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);
        api.MapDelete("/device/notes/{noteId:guid}", DeleteAsync).Terminal().RequireRateLimiting(TerminalEndpoints.DeviceRateLimit);

        // Central: ler as notas de uma pessoa é leitura de uso individual; colar recado é gerenciar a máquina.
        api.MapGet("/devices/{id:guid}/notes", PanelListAsync).Panel(Permission.UsageReadIndividual);
        api.MapPost("/devices/{id:guid}/notes", PanelSendAsync).Panel(Permission.DevicesManage);
        api.MapDelete("/devices/{id:guid}/notes/{noteId:guid}", PanelRemoveAsync).Panel(Permission.DevicesManage);
    }

    private static object Dto(StickyNote note) => new
    {
        id = note.Id,
        color = note.Color,
        content = NoteContent.Parse(note.Content),
        x = note.X,
        y = note.Y,
        width = note.Width,
        height = note.Height,
        open = note.Open,
        from = note.FromAdminName,
        createdAt = note.CreatedAt,
        updatedAt = note.UpdatedAt,
    };

    private static Device ApprovedDevice(HttpContext context)
    {
        var device = context.Device();
        // Sem aprovação não há notas; o Terminal entende o 403 como "ainda não" (tenta depois), e não como "lista vazia".
        return device.Approved ? device : throw ApiError.Forbidden("A máquina ainda não foi aprovada.");
    }

    private static async Task<IResult> ListAsync(HttpContext context, PontualDb db, TimeProvider time, CancellationToken ct)
    {
        var device = ApprovedDevice(context);
        var all = await db.StickyNotes.Where(n => n.DeviceId == device.Id).ToListAsync(ct);

        var limit = time.GetUtcNow().UtcDateTime - RemovedKept;
        var expired = all.Where(n => n.DeletedAt is { } at && at < limit).ToList();
        if (expired.Count > 0)
        {
            db.StickyNotes.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new
        {
            notes = all.Where(n => n.DeletedAt is null).OrderBy(n => n.CreatedAt).Select(Dto),
            // Recados que o TI retirou: o Terminal tira da tela e da lista.
            removed = all.Where(n => n.DeletedAt is not null).Select(n => n.Id),
        });
    }

    private static async Task<IResult> SaveAsync(Guid noteId, HttpContext context, PontualDb db, TimeProvider time, CancellationToken ct)
    {
        var device = ApprovedDevice(context);
        var body = await context.ReadBodyAsync<NoteBody>();
        var content = NoteContent.Normalize(body.Content);
        var now = time.GetUtcNow().UtcDateTime;

        var note = await db.StickyNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is not null && (note.DeviceId != device.Id || note.DeletedAt is not null))
        {
            // De outro computador, ou um recado que o TI já retirou: não volta.
            throw ApiError.Conflict("Esta nota não pode ser gravada neste computador.");
        }

        if (note is null)
        {
            if (await db.StickyNotes.CountAsync(n => n.DeviceId == device.Id && n.DeletedAt == null, ct) >= MaxNotesPerDevice)
            {
                throw ApiError.Conflict($"Cada computador guarda no máximo {MaxNotesPerDevice} notas.");
            }

            note = new StickyNote { Id = noteId, DeviceId = device.Id, CreatedAt = now };
            db.StickyNotes.Add(note);
        }

        note.Color = NoteContent.Color(body.Color);
        note.Content = content;
        note.X = Math.Clamp(body.X, -20_000, 20_000);
        note.Y = Math.Clamp(body.Y, -20_000, 20_000);
        note.Width = Math.Clamp(body.Width, 0, 4_000);
        note.Height = Math.Clamp(body.Height, 0, 4_000);
        note.Open = body.Open;
        note.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.Ok(Dto(note));
    }

    private static async Task<IResult> DeleteAsync(Guid noteId, HttpContext context, PontualDb db, CancellationToken ct)
    {
        var device = ApprovedDevice(context);
        // Apagar o que já não existe não é erro: o Terminal repete o pedido se ficou sem resposta.
        await db.StickyNotes.Where(n => n.Id == noteId && n.DeviceId == device.Id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static async Task<Device> FindDeviceAsync(PontualDb db, Guid id, CancellationToken ct) =>
        await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");

    private static async Task<IResult> PanelListAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await FindDeviceAsync(db, id, ct);
        var notes = await db.StickyNotes.Where(n => n.DeviceId == id && n.DeletedAt == null).ToListAsync(ct);

        // Ler as notas de uma pessoa fica registrado (quem e quando), como a visualização da tela. O texto não entra.
        await audit.SaveAsync(db, AuditInfo.By(admin, "notes.view",
            $"{admin.Name} abriu as notas adesivas de {device.Label}", "device", device.Id), cancellationToken: ct);

        return Results.Ok(notes.OrderByDescending(n => n.UpdatedAt).Select(Dto));
    }

    private static async Task<IResult> PanelSendAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await FindDeviceAsync(db, id, ct);
        if (!device.Approved)
        {
            throw ApiError.Conflict("A máquina ainda não foi aprovada.");
        }

        var body = await context.ReadBodyAsync<SendBody>();
        var text = (body.Text ?? "").Trim();
        if (text.Length == 0)
        {
            throw ApiError.BadRequest("Escreva o recado.");
        }

        if (text.Length > 2000)
        {
            throw ApiError.BadRequest("O recado aceita no máximo 2000 caracteres.");
        }

        if (await db.StickyNotes.CountAsync(n => n.DeviceId == id && n.DeletedAt == null, ct) >= MaxNotesPerDevice)
        {
            throw ApiError.Conflict($"Este computador já tem {MaxNotesPerDevice} notas. Peça para a atendente apagar alguma.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var note = new StickyNote
        {
            Id = Guid.NewGuid(), DeviceId = id, Color = NoteContent.Color(body.Color), Content = NoteContent.FromText(text),
            Open = true, FromAdminName = admin.Name, CreatedAt = now, UpdatedAt = now,
        };
        db.StickyNotes.Add(note);

        await audit.SaveAsync(db, AuditInfo.By(admin, "notes.send",
            $"{admin.Name} colou um recado na tela de {device.Label}", "device", device.Id), cancellationToken: ct);

        return Results.Ok(Dto(note));
    }

    /// <summary>O TI só retira os recados que o próprio TI colou; as notas da atendente são dela.</summary>
    private static async Task<IResult> PanelRemoveAsync(Guid id, Guid noteId, HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await FindDeviceAsync(db, id, ct);
        var note = await db.StickyNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.DeviceId == id && n.DeletedAt == null, ct)
            ?? throw ApiError.NotFound("Nota não encontrada.");
        if (note.FromAdminName.Length == 0)
        {
            throw ApiError.Forbidden("Esta nota é da atendente. Pela Central só dá para retirar os recados enviados pelo TI.");
        }

        note.DeletedAt = time.GetUtcNow().UtcDateTime;
        await audit.SaveAsync(db, AuditInfo.By(admin, "notes.remove",
            $"{admin.Name} retirou um recado da tela de {device.Label}", "device", device.Id), cancellationToken: ct);

        return Results.NoContent();
    }
}
