using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Audit;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

public sealed record AuditInfo(
    ActorType ActorType,
    Guid? ActorId,
    string ActorName,
    string Code,
    string Text,
    string? TargetType = null,
    Guid? TargetId = null,
    string? Justification = null)
{
    public static AuditInfo By(Admin admin, string code, string text, string? targetType = null, Guid? targetId = null, string? justification = null) =>
        new(ActorType.Admin, admin.Id, admin.Name, code, text, targetType, targetId, justification);
}

/// <summary>
/// Grava o registro de auditoria junto com as demais mudanças pendentes no contexto, numa única transação.
/// As gravações são serializadas: ler a cabeça da cadeia e inserir o próximo elo acontecem sob o mesmo
/// semáforo, então a cadeia não se ramifica. A chave primária em <c>Sequence</c> é a segunda barreira.
/// </summary>
public sealed class AuditWriter(TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="bumpConfig">Incrementa a versão global da configuração na mesma transação.</param>
    public async Task SaveAsync(PontualDb db, AuditInfo info, bool bumpConfig = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var head = await db.Audit.AsNoTracking()
                .OrderByDescending(a => a.Sequence)
                .FirstOrDefaultAsync(cancellationToken);

            var entry = AuditChain.Append(head?.ToEntry(), new AuditEntryData(
                time.GetUtcNow(),
                info.ActorType,
                info.ActorId,
                AuditRecord.ChainAction(info.Code, info.ActorName, info.Text),
                info.TargetType,
                info.TargetId,
                info.Justification));

            db.Audit.Add(new AuditRecord
            {
                Sequence = entry.Sequence,
                OccurredAtTicks = entry.OccurredAt.UtcTicks,
                ActorType = entry.ActorType,
                ActorId = entry.ActorId,
                ActorName = info.ActorName,
                Code = info.Code,
                Text = info.Text,
                TargetType = entry.TargetType,
                TargetId = entry.TargetId,
                Justification = entry.Justification,
                PreviousHash = [.. entry.PreviousHash],
                Hash = [.. entry.Hash],
            });

            if (bumpConfig)
            {
                var version = await db.Settings.SingleAsync(s => s.Key == Setting.ConfigVersion, cancellationToken);
                version.Value++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
