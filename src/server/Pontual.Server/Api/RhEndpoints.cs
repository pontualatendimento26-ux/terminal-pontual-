using Pontual.Domain.Authorization;
using Pontual.Server.Data;
using Pontual.Server.Rh;

namespace Pontual.Server.Api;

/// <summary>
/// As rotas do Agente RH (<c>/api/v1/rh/...</c>): a porta por onde ele lê a Central Neural (ver <see cref="IRhCentral"/>).
/// Só o superadministrador entra (decisão do dono). Tudo aqui é leitura; os casos de atendimento saem sempre anônimos.
/// </summary>
public static class RhEndpoints
{
    private static readonly Permission Access = Permission.ConfigEdit;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/rh", StatusAsync).Panel(Access);
        api.MapGet("/rh/strengths", StrengthsAsync).Panel(Access);
        api.MapGet("/rh/knowledge", SearchAsync).Panel(Access);
        api.MapGet("/rh/knowledge/{id:guid}", KnowledgeAsync).Panel(Access);
        api.MapGet("/rh/cases", CasesAsync).Panel(Access);
        api.MapGet("/rh/cases/{id}", CaseAsync).Panel(Access);
    }

    /// <summary>O Agente RH (e a atendente ideal, que é dele) é só do superadministrador.</summary>
    public static Admin Demand(HttpContext context)
    {
        var admin = context.Admin();
        return admin.Role == Role.SuperAdmin ? admin : throw ApiError.Forbidden("O Agente RH é só para o superadministrador.");
    }

    private static async Task<IResult> StatusAsync(HttpContext context, IRhCentral central, CancellationToken ct)
    {
        Demand(context);
        var status = await central.StatusAsync(ct);
        return Results.Ok(new { attendants = status.Attendants, strengths = status.Strengths, knowledge = status.Knowledge, cases = status.Cases });
    }

    private static async Task<IResult> StrengthsAsync(HttpContext context, IRhCentral central, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = Demand(context);
        var strengths = await central.StrengthsAsync(ct);
        if (strengths.Count > 0)
        {
            await audit.SaveAsync(db, AuditInfo.By(admin, "rh.strengths.read", $"{admin.Name} leu os pontos fortes das atendentes pelo Agente RH"), cancellationToken: ct);
        }

        return Results.Ok(strengths.Select(s => new { name = s.Name, strengths = s.Strengths, profile = s.Profile, updatedAt = s.UpdatedAt }));
    }

    private static async Task<IResult> SearchAsync(string? q, int? max, HttpContext context, IRhCentral central, CancellationToken ct)
    {
        Demand(context);
        var hits = await central.SearchKnowledgeAsync(Input.Truncated(q, 200), max ?? 20, ct);
        return Results.Ok(hits.Select(h => new { id = h.Id, kind = h.Kind, title = h.Title, bank = h.Bank, part = h.Part, parts = h.Parts, snippet = h.Snippet }));
    }

    private static async Task<IResult> KnowledgeAsync(Guid id, HttpContext context, IRhCentral central, CancellationToken ct)
    {
        Demand(context);
        var item = await central.KnowledgeAsync(id, ct) ?? throw ApiError.NotFound("Conhecimento não encontrado, desativado ou reservado às atendentes.");
        return Results.Ok(new { id = item.Id, kind = item.Kind, title = item.Title, bank = item.Bank, content = item.Content });
    }

    private static async Task<IResult> CasesAsync(int? limit, HttpContext context, IRhCentral central, CancellationToken ct)
    {
        Demand(context);
        var cases = await central.CasesAsync(limit ?? 30, ct);
        return Results.Ok(cases.Select(c => new { id = c.Id, lines = c.Lines, clientLines = c.ClientLines, attendantLines = c.AttendantLines, days = c.Days }));
    }

    private static async Task<IResult> CaseAsync(string id, HttpContext context, IRhCentral central, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = Demand(context);
        var found = await central.CaseAsync(Input.Truncated(id, 40), ct) ?? throw ApiError.NotFound("Caso não encontrado. As conversas do CRM ficam 30 dias na Central.");
        await audit.SaveAsync(db, AuditInfo.By(admin, "rh.case.read", $"{admin.Name} leu um caso de atendimento anônimo ({found.Id}) pelo Agente RH"), cancellationToken: ct);
        return Results.Ok(new { id = found.Id, lines = found.Lines, text = found.Text });
    }
}
