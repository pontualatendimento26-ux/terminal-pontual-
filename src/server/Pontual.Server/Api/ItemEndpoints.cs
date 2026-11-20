using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>Sites e programas (contrato, seção 4).</summary>
public static class ItemEndpoints
{
    private sealed record NewSiteBody(string? Name, string? Url, string? Group, List<Guid>? Audience, List<string?>? ExtraHosts);
    private sealed record NewProgramBody(string? Name, string? Path, string? Reason, List<Guid>? Audience);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/items", async (PontualDb db, CancellationToken ct) =>
        {
            var items = await Ordered(db.Items.AsNoTracking()).ToListAsync(ct);
            return Results.Ok(items.Select(ItemDto.From));
        }).Panel(Permission.AppsRead);

        api.MapPost("/items/sites", CreateSiteAsync).Panel(Permission.AppsWebsitesManage);
        api.MapPost("/items/programs", ProposeProgramAsync).Panel(Permission.AppsExecutablesPropose);
        api.MapPost("/items/{id:guid}/approve", ApproveAsync).Panel(Permission.AppsExecutablesApprove);
        api.MapPost("/items/{id:guid}/reject", RejectAsync).Panel(Permission.AppsExecutablesApprove);

        // A permissão depende do tipo do item, então é conferida dentro da rota.
        api.MapPatch("/items/{id:guid}", PatchAsync).PanelLoggedIn();
        api.MapDelete("/items/{id:guid}", DeleteAsync).PanelLoggedIn();
    }

    public static IQueryable<Item> Ordered(IQueryable<Item> items) => items.OrderBy(i => i.Order).ThenBy(i => i.CreatedAt);

    private static async Task<Item> FindAsync(PontualDb db, Guid id, CancellationToken ct) =>
        await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw ApiError.NotFound("Item não encontrado.");

    private static Permission ManagePermission(Item item) =>
        item.Kind == ItemKind.Site ? Permission.AppsWebsitesManage : Permission.AppsExecutablesApprove;

    private static async Task EnsureNameIsFreeAsync(PontualDb db, string name, Guid? except, CancellationToken ct)
    {
        // Os eventos do terminal citam o item pelo nome; dois itens com o mesmo nome ficariam indistinguíveis.
        var lower = name.ToLowerInvariant();
        var names = await db.Items.Where(i => i.Id != except).Select(i => i.Name).ToListAsync(ct);
        if (names.Any(n => n.ToLowerInvariant() == lower))
        {
            throw ApiError.Conflict($"Já existe um item chamado {name}.");
        }
    }

    /// <summary>Valida o público e devolve os rótulos das máquinas, para o texto da auditoria.</summary>
    private static async Task<(List<Guid>? Audience, Dictionary<Guid, string> Labels)> AudienceAsync(PontualDb db, List<Guid>? requested, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        var labels = devices.ToDictionary(d => d.Id, d => d.Label);
        if (requested is null)
        {
            return (null, labels);
        }

        if (requested.Count > 1000)
        {
            throw ApiError.BadRequest("Público grande demais.");
        }

        var audience = requested.Distinct().ToList();
        if (audience.Any(id => !labels.ContainsKey(id)))
        {
            throw ApiError.BadRequest("O público indica uma máquina que não existe.");
        }

        return (audience, labels);
    }

    private static string DescribeAudience(List<Guid>? audience, Dictionary<Guid, string> labels) =>
        audience is null ? "todas as máquinas"
        : audience.Count == 0 ? "nenhuma máquina"
        : string.Join(", ", audience.Select(id => labels.GetValueOrDefault(id, "máquina removida")));

    private static async Task<int> NextOrderAsync(PontualDb db, CancellationToken ct) =>
        (await db.Items.MaxAsync(i => (int?)i.Order, ct) ?? 0) + 1;

    private static async Task<IResult> CreateSiteAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<NewSiteBody>();
        var name = Input.Required(body.Name, "o nome do site", 60);
        var url = Input.SiteUrl(body.Url);
        var group = Input.Group(body.Group);
        var extraHosts = Input.Hosts(body.ExtraHosts);
        var (audience, labels) = await AudienceAsync(db, body.Audience, ct);
        await EnsureNameIsFreeAsync(db, name, null, ct);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = ItemKind.Site,
            Target = url,
            Group = group,
            ExtraHosts = extraHosts,
            Audience = audience,
            Order = await NextOrderAsync(db, ct),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Items.Add(item);

        await audit.SaveAsync(db, AuditInfo.By(admin, "item.site.create",
            $"{admin.Name} cadastrou o site {name} ({url}) e liberou para {DescribeAudience(audience, labels)}", "item", item.Id), bumpConfig: true, ct);

        return Results.Ok(ItemDto.From(item));
    }

    private static async Task<IResult> ProposeProgramAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<NewProgramBody>();
        var name = Input.Required(body.Name, "o nome do programa", 60);
        var path = Input.ProgramPath(body.Path);
        var reason = Input.Required(body.Reason, "o motivo", 300);
        var (audience, labels) = await AudienceAsync(db, body.Audience, ct);
        await EnsureNameIsFreeAsync(db, name, null, ct);

        // Decisão do dono (20/11/2026): o superadmin libera programa direto, sem a aprovação de outro administrador.
        // Continua pedindo senha e motivo, e a auditoria registra que não houve segunda aprovação.
        var direct = admin.Role == Role.SuperAdmin;
        var now = time.GetUtcNow().UtcDateTime;
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = ItemKind.Program,
            Target = path,
            Group = Input.Groups[2],
            Audience = audience,
            Pending = !direct,
            ProposedById = admin.Id,
            ProposedByName = admin.Name,
            ProposedAt = now,
            ProposalReason = reason,
            Order = await NextOrderAsync(db, ct),
            CreatedAt = now,
        };
        db.Items.Add(item);

        if (direct)
        {
            await audit.SaveAsync(db, AuditInfo.By(admin, "item.program.create",
                $"{admin.Name} liberou o programa {name} ({path}) para {DescribeAudience(audience, labels)} direto, como superadmin, sem segunda aprovação: {reason}",
                "item", item.Id, reason), bumpConfig: true, ct);
            return Results.Ok(ItemDto.From(item));
        }

        // Proposta pendente não chega ao terminal, então a versão da configuração não muda.
        await audit.SaveAsync(db, AuditInfo.By(admin, "item.program.propose",
            $"{admin.Name} propôs liberar o programa {name} ({path}) para {DescribeAudience(audience, labels)}: {reason}", "item", item.Id, reason), cancellationToken: ct);

        return Results.Ok(ItemDto.From(item));
    }

    private static async Task<Item> FindPendingAsync(PontualDb db, Guid id, CancellationToken ct)
    {
        var item = await FindAsync(db, id, ct);
        if (!item.Pending)
        {
            throw ApiError.Conflict("Este item não está aguardando aprovação.");
        }

        return item;
    }

    private static async Task<IResult> ApproveAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var item = await FindPendingAsync(db, id, ct);
        if (item.ProposedById == admin.Id && admin.Role != Role.SuperAdmin)
        {
            throw ApiError.Forbidden("Quem propôs o programa não pode aprová-lo. Peça a outro administrador.");
        }

        // A lista de recusados pode ter mudado desde a proposta.
        Input.ProgramPath(item.Target);

        var proposer = item.ProposedByName;
        var own = item.ProposedById == admin.Id;
        item.Pending = false;
        item.Enabled = true;

        await audit.SaveAsync(db, AuditInfo.By(admin, "item.program.approve",
            own
                ? $"{admin.Name} aprovou o próprio programa {item.Name} ({item.Target}), como superadmin, sem segunda aprovação"
                : $"{admin.Name} aprovou o programa {item.Name} ({item.Target}), proposto por {proposer}", "item", item.Id), bumpConfig: true, ct);

        return Results.Ok(ItemDto.From(item));
    }

    private static async Task<IResult> RejectAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var item = await FindPendingAsync(db, id, ct);
        db.Items.Remove(item);

        await audit.SaveAsync(db, AuditInfo.By(admin, "item.program.reject",
            $"{admin.Name} recusou o programa {item.Name} ({item.Target}), proposto por {item.ProposedByName}", "item", item.Id), cancellationToken: ct);

        return Results.NoContent();
    }

    private static async Task<IResult> PatchAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var body = await context.ReadBodyAsync<JsonElement>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiError.BadRequest("Os dados enviados não estão no formato esperado.");
        }

        var item = await FindAsync(db, id, ct);
        context.Demand(ManagePermission(item));

        if (item.Pending)
        {
            throw ApiError.Conflict("O programa ainda aguarda aprovação.");
        }

        var isSite = item.Kind == ItemKind.Site;
        if (!isSite && (body.TryGetProperty("name", out _) || body.TryGetProperty("extraHosts", out _) || body.TryGetProperty("group", out _)))
        {
            throw ApiError.BadRequest("Em um programa só é possível mudar o bloqueio e o público.");
        }

        var changes = new List<string>();
        var oldName = item.Name;

        try
        {
            if (body.TryGetProperty("name", out var nameJson))
            {
                var name = Input.Required(nameJson.GetString(), "o nome do site", 60);
                if (name != item.Name)
                {
                    await EnsureNameIsFreeAsync(db, name, item.Id, ct);
                    item.Name = name;
                    changes.Add($"renomeou {oldName} para {name}");
                }
            }

            if (body.TryGetProperty("group", out var groupJson))
            {
                var group = Input.Group(groupJson.GetString());
                if (group != item.Group)
                {
                    item.Group = group;
                    changes.Add($"moveu {item.Name} para o grupo {group}");
                }
            }

            if (body.TryGetProperty("extraHosts", out var hostsJson))
            {
                var hosts = Input.Hosts(hostsJson.Deserialize<List<string?>>() ?? []);
                if (!hosts.SequenceEqual(item.ExtraHosts))
                {
                    item.ExtraHosts = hosts;
                    changes.Add(hosts.Count == 0
                        ? $"retirou os domínios extras de {item.Name}"
                        : $"definiu os domínios extras de {item.Name}: {string.Join(", ", hosts)}");
                }
            }

            if (body.TryGetProperty("enabled", out var enabledJson))
            {
                var enabled = enabledJson.GetBoolean();
                if (enabled != item.Enabled)
                {
                    item.Enabled = enabled;
                    changes.Add($"{(enabled ? "desbloqueou" : "bloqueou")} {item.Name}");
                }
            }

            if (body.TryGetProperty("audience", out var audienceJson))
            {
                var (audience, labels) = await AudienceAsync(db, audienceJson.Deserialize<List<Guid>?>(), ct);
                var before = item.Audience;
                if (!SameAudience(before, audience))
                {
                    item.Audience = audience;
                    changes.Add(DescribeAudienceChange(item.Name, before, audience, labels));
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw ApiError.BadRequest("Os dados enviados não estão no formato esperado.");
        }

        if (changes.Count > 0)
        {
            await audit.SaveAsync(db, AuditInfo.By(admin, "item.update",
                $"{admin.Name} {string.Join("; ", changes)}", "item", item.Id), bumpConfig: true, ct);
        }

        return Results.Ok(ItemDto.From(item));
    }

    private static bool SameAudience(List<Guid>? left, List<Guid>? right) =>
        left is null || right is null ? left is null && right is null : left.ToHashSet().SetEquals(right);

    private static string DescribeAudienceChange(string name, List<Guid>? before, List<Guid>? after, Dictionary<Guid, string> labels)
    {
        if (after is null)
        {
            return $"liberou {name} para todas as máquinas";
        }

        if (before is null)
        {
            return $"restringiu {name} a {DescribeAudience(after, labels)}";
        }

        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        var parts = new List<string>();
        if (added.Count > 0)
        {
            parts.Add($"liberou {name} para {DescribeAudience(added, labels)}");
        }

        if (removed.Count > 0)
        {
            parts.Add($"retirou {name} de {DescribeAudience(removed, labels)}");
        }

        return string.Join(" e ", parts);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var item = await FindAsync(db, id, ct);
        context.Demand(ManagePermission(item));

        foreach (var observed in await db.ObservedHosts.Where(o => o.ItemId == id).ToListAsync(ct))
        {
            observed.ItemId = null;
        }

        db.Items.Remove(item);

        var what = item.Kind == ItemKind.Site ? "o site" : item.Pending ? "a proposta do programa" : "o programa";
        await audit.SaveAsync(db, AuditInfo.By(admin, "item.delete",
            $"{admin.Name} excluiu {what} {item.Name} ({item.Target})", "item", item.Id), bumpConfig: !item.Pending, ct);

        return Results.NoContent();
    }
}
