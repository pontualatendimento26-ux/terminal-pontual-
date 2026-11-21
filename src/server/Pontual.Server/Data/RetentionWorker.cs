using Microsoft.EntityFrameworkCore;

namespace Pontual.Server.Data;

/// <summary>
/// Limpeza automática do que só cresce: uma vez por dia apaga o que passou do prazo.
/// A auditoria NÃO é apagada: ela é uma cadeia de hashes, e tirar o começo quebraria a verificação.
/// </summary>
public sealed class RetentionWorker(IServiceProvider services, ILogger<RetentionWorker> logger, TimeProvider time) : BackgroundService
{
    /// <summary>Histórico de cada máquina (sites e aplicativos abertos).</summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(90);
    public static readonly TimeSpan Notifications = TimeSpan.FromDays(60);
    /// <summary>Só alertas já resolvidos; os abertos ficam até alguém resolver.</summary>
    public static readonly TimeSpan ResolvedAlerts = TimeSpan.FromDays(180);
    /// <summary>Conversas com a assistente que a análise não chegou a ler e apagar.</summary>
    public static readonly TimeSpan Conversations = TimeSpan.FromDays(60);
    /// <summary>Respostas do WhatsApp já concluídas (enviadas, recusadas, puladas, com falha, ensaio).</summary>
    public static readonly TimeSpan FinishedReplies = TimeSpan.FromDays(90);

    private static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    private static readonly string[] Finished =
    [
        CoverageStatus.Sent, CoverageStatus.Rejected, CoverageStatus.Failed, CoverageStatus.Skipped, CoverageStatus.Draft,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstWait, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "A limpeza diária falhou; a Central segue e tenta de novo amanhã.");
                }

                await Task.Delay(Every, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Servidor encerrando.
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
        var now = time.GetUtcNow().UtcDateTime;

        var history = now - History;
        var notifications = now - Notifications;
        var alerts = now - ResolvedAlerts;
        var conversations = now - Conversations;
        var replies = now - FinishedReplies;

        var removed =
            await db.History.Where(h => h.At < history).ExecuteDeleteAsync(ct)
            + await db.Notifications.Where(n => n.At < notifications).ExecuteDeleteAsync(ct)
            + await db.Alerts.Where(a => a.ResolvedAt != null && a.ResolvedAt < alerts).ExecuteDeleteAsync(ct)
            + await db.AssistantMessages.Where(m => m.At < conversations).ExecuteDeleteAsync(ct)
            + await db.CoverageReplies.Where(r => Finished.Contains(r.Status) && r.UpdatedAt < replies).ExecuteDeleteAsync(ct);

        if (removed > 0)
        {
            logger.LogInformation("Limpeza diária: {Count} registros antigos apagados.", removed);
        }
    }
}
