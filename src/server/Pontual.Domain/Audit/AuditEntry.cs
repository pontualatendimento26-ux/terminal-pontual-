using System.Collections.Immutable;

namespace Pontual.Domain.Audit;

/// <summary>Registro já encadeado. Crie com <see cref="AuditChain.Append"/>.</summary>
public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset OccurredAt,
    ActorType ActorType,
    Guid? ActorId,
    string Action,
    string? TargetType,
    Guid? TargetId,
    string? Justification,
    ImmutableArray<byte> PreviousHash,
    ImmutableArray<byte> Hash)
{
    // A igualdade gerada para records compara ImmutableArray pela referência do array interno;
    // dois registros lidos do banco com os mesmos bytes seriam considerados diferentes.
    public bool Equals(AuditEntry? other) =>
        other is not null
        && Sequence == other.Sequence
        && OccurredAt == other.OccurredAt
        && ActorType == other.ActorType
        && ActorId == other.ActorId
        && string.Equals(Action, other.Action, StringComparison.Ordinal)
        && string.Equals(TargetType, other.TargetType, StringComparison.Ordinal)
        && TargetId == other.TargetId
        && string.Equals(Justification, other.Justification, StringComparison.Ordinal)
        && PreviousHash.AsSpan().SequenceEqual(other.PreviousHash.AsSpan())
        && Hash.AsSpan().SequenceEqual(other.Hash.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Sequence);
        hash.AddBytes(Hash.AsSpan());
        return hash.ToHashCode();
    }
}
