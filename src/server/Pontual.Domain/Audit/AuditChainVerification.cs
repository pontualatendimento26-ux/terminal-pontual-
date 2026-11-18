using System.Diagnostics.CodeAnalysis;

namespace Pontual.Domain.Audit;

public enum AuditChainFailure
{
    /// <summary>O hash gravado não corresponde ao conteúdo do registro.</summary>
    HashMismatch,

    /// <summary>O hash anterior gravado não é o hash do registro que o precede.</summary>
    PreviousHashMismatch,

    /// <summary>A sequência não é a anterior + 1 (ou não começa em 1).</summary>
    SequenceGap,
}

/// <summary>Resultado de <see cref="AuditChain.Verify"/>: íntegra, ou o primeiro registro que falhou e o motivo.</summary>
public sealed record AuditChainVerification
{
    private AuditChainVerification(long? failedSequence, AuditChainFailure? failure)
    {
        FailedSequence = failedSequence;
        Failure = failure;
    }

    public static AuditChainVerification Intact { get; } = new(null, null);

    [MemberNotNullWhen(false, nameof(FailedSequence), nameof(Failure))]
    public bool IsIntact => Failure is null;

    /// <summary>Sequência gravada no primeiro registro que falhou.</summary>
    public long? FailedSequence { get; }

    public AuditChainFailure? Failure { get; }

    public static AuditChainVerification Broken(long failedSequence, AuditChainFailure failure) =>
        new(failedSequence, failure);
}
