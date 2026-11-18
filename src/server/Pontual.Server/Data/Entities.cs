using System.Text.Json;
using Pontual.Domain.Audit;
using Pontual.Domain.Authorization;

namespace Pontual.Server.Data;

// Todas as datas são DateTime em UTC (o SQLite não ordena nem compara DateTimeOffset).

public sealed class Admin
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Sempre em minúsculas.</summary>
    public required string Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public Role Role { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public sealed class AdminSession
{
    public Guid Id { get; set; }
    /// <summary>SHA-256 (hex) do valor do cookie.</summary>
    public required string TokenHash { get; set; }
    public Guid AdminId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public required string Hostname { get; set; }
    public string? Windows { get; set; }
    /// <summary>SHA-256 (hex) do token; o token em si nunca é gravado.</summary>
    public required string TokenHash { get; set; }
    public string? AttendantName { get; set; }
    public bool Approved { get; set; }
    public bool ObservationMode { get; set; } = true;
    public DateTime? LastSeenAt { get; set; }
    public DateTime? SessionStartedAt { get; set; }
    public string AppsInUseJson { get; set; } = "[]";
    public string? MaintenanceReason { get; set; }
    public DateTime? MaintenanceUntil { get; set; }
    public DateTime CreatedAt { get; set; }

    public string Label => string.IsNullOrEmpty(AttendantName) ? Hostname : $"{AttendantName} ({Hostname})";

    public List<string> AppsInUse
    {
        get => JsonList.Read<string>(AppsInUseJson);
        set => AppsInUseJson = JsonList.Write(value);
    }

    public bool InMaintenance(DateTime now) => MaintenanceUntil is { } until && until > now;
}

public sealed class EnrollmentCode
{
    public Guid Id { get; set; }
    /// <summary>SHA-256 (hex) do código sem o hífen.</summary>
    public required string CodeHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid CreatedById { get; set; }
}

public static class ItemKind
{
    public const string Site = "site";
    public const string Program = "program";
}

public sealed class Item
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string Target { get; set; }
    public required string Group { get; set; }
    public string ExtraHostsJson { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    /// <summary>Programa aguardando a segunda aprovação.</summary>
    public bool Pending { get; set; }
    /// <summary>Nulo = todas as máquinas; senão, lista JSON de ids de máquina.</summary>
    public string? AudienceJson { get; set; }
    public Guid? ProposedById { get; set; }
    public string? ProposedByName { get; set; }
    public DateTime? ProposedAt { get; set; }
    public string? ProposalReason { get; set; }
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<string> ExtraHosts
    {
        get => JsonList.Read<string>(ExtraHostsJson);
        set => ExtraHostsJson = JsonList.Write(value);
    }

    public List<Guid>? Audience
    {
        get => AudienceJson is null ? null : JsonList.Read<Guid>(AudienceJson);
        set => AudienceJson = value is null ? null : JsonList.Write(value);
    }

    public string Status => Pending ? "pending" : Enabled ? "active" : "blocked";

    public bool VisibleTo(Guid deviceId) =>
        !Pending && Enabled && (Audience is not { } audience || audience.Contains(deviceId));

    /// <summary>Domínio do destino mais os domínios extras; vazio para programas.</summary>
    public List<string> Hosts()
    {
        if (Kind != ItemKind.Site)
        {
            return [];
        }

        var hosts = new List<string>();
        if (Uri.TryCreate(Target, UriKind.Absolute, out var uri))
        {
            hosts.Add(uri.Host.ToLowerInvariant());
        }

        foreach (var extra in ExtraHosts)
        {
            if (!hosts.Contains(extra))
            {
                hosts.Add(extra);
            }
        }

        return hosts;
    }
}

public sealed class Alert
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public required string Text { get; set; }
    public DateTime At { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public sealed class Notification
{
    /// <summary>Sequencial: também dá a ordem de criação entre notificações do mesmo instante.</summary>
    public long Id { get; set; }
    public DateTime At { get; set; }
    public required string Text { get; set; }
    public bool Urgent { get; set; }
    public bool Read { get; set; }
}

public sealed class HistoryEntry
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime At { get; set; }
    public required string Text { get; set; }
}

public sealed class ObservedHost
{
    public Guid Id { get; set; }
    public required string Host { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? ItemId { get; set; }
    public string ItemName { get; set; } = "";
    public int Count { get; set; }
    public DateTime LastAt { get; set; }
}

public sealed class Setting
{
    public const string ConfigVersion = "configVersion";
    public const string Seeded = "seeded";

    public required string Key { get; set; }
    public long Value { get; set; }
}

/// <summary>Configuração em texto (as de <see cref="Setting"/> são só números).</summary>
public sealed class TextSetting
{
    /// <summary>Chave da API da IA, guardada cifrada (ver <c>AssistantSecrets</c>).</summary>
    public const string AssistantApiKey = "assistant.apiKey";
    public const string AssistantModel = "assistant.model";
    /// <summary>Orientações gerais da Pontual, válidas para todas as atendentes.</summary>
    public const string AssistantInstructions = "assistant.instructions";
    /// <summary>Marca de que os roteiros iniciais já foram carregados (não voltam se a Central apagar algum).</summary>
    public const string AssistantScriptsSeeded = "assistant.scriptsSeeded";

    public required string Key { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>Roteiro operacional (de um banco, em geral) que a assistente usa para responder.</summary>
public sealed class AssistantScript
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
    public string UpdatedByName { get; set; } = "";
}

/// <summary>Perfil da assistente de uma atendente (uma máquina = uma atendente, ADR 0003).</summary>
public sealed class AssistantProfile
{
    public Guid DeviceId { get; set; }
    /// <summary>Orientações da supervisão só para esta atendente.</summary>
    public string Instructions { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

public static class AssistantRole
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>Uma fala da conversa da atendente com a assistente.</summary>
public sealed class AssistantMessage
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>"Nova conversa" troca este valor: só as falas da conversa atual voltam para a IA como contexto.</summary>
    public Guid ConversationId { get; set; }
    public required string Role { get; set; }
    public required string Text { get; set; }
    public DateTime At { get; set; }
}

/// <summary>
/// Registro de auditoria gravado. Guarda tudo o que <see cref="AuditChain"/> usou no hash, para a cadeia
/// poder ser reconstruída e verificada.
/// </summary>
public sealed class AuditRecord
{
    public long Sequence { get; set; }
    /// <summary>Ticks UTC, já na precisão de microssegundo usada pela cadeia.</summary>
    public long OccurredAtTicks { get; set; }
    public ActorType ActorType { get; set; }
    public Guid? ActorId { get; set; }
    public required string ActorName { get; set; }
    /// <summary>Código estável da ação, ex.: <c>device.approve</c>.</summary>
    public required string Code { get; set; }
    /// <summary>Texto legível mostrado no painel.</summary>
    public required string Text { get; set; }
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public string? Justification { get; set; }
    public required byte[] PreviousHash { get; set; }
    public required byte[] Hash { get; set; }

    public DateTime OccurredAt => new(OccurredAtTicks, DateTimeKind.Utc);

    /// <summary>
    /// O campo "ação" da cadeia leva código, nome do ator e texto, para que tudo o que o painel mostra
    /// esteja coberto pelo hash. Nenhum dos três contém quebra de linha (a entrada é saneada).
    /// </summary>
    public static string ChainAction(string code, string actorName, string text) => $"{code}\n{actorName}\n{text}";

    public AuditEntry ToEntry() => new(
        Sequence,
        new DateTimeOffset(OccurredAtTicks, TimeSpan.Zero),
        ActorType,
        ActorId,
        ChainAction(Code, ActorName, Text),
        TargetType,
        TargetId,
        Justification,
        [.. PreviousHash],
        [.. Hash]);
}

internal static class JsonList
{
    public static List<T> Read<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Write<T>(IEnumerable<T> values) => JsonSerializer.Serialize(values);
}
