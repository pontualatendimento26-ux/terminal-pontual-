namespace Pontual.Domain.Audit;

/// <summary>Dados de um novo registro, antes de entrar na cadeia.</summary>
public sealed record AuditEntryData(
    DateTimeOffset OccurredAt,
    ActorType ActorType,
    Guid? ActorId,
    string Action,
    string? TargetType = null,
    Guid? TargetId = null,
    string? Justification = null);
