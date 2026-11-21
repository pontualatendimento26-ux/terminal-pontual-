using Anthropic.Models.Messages;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>Soma o consumo das chamadas de um pedido (no modo índice são várias).</summary>
public sealed class UsageMeter
{
    public int Calls { get; private set; }
    public long Input { get; private set; }
    public long CacheRead { get; private set; }
    public long CacheWrite { get; private set; }
    public long Output { get; private set; }

    // Saída já contada da chamada em fluxo atual, para trocar pelo total quando o fim chega.
    private long _streamOutput;

    public void Add(Usage usage)
    {
        Calls++;
        Input += usage.InputTokens;
        CacheRead += usage.CacheReadInputTokens ?? 0;
        CacheWrite += usage.CacheCreationInputTokens ?? 0;
        Output += usage.OutputTokens;
        _streamOutput = usage.OutputTokens;
    }

    /// <summary>No fluxo, o evento final traz a saída acumulada da chamada; troca a parcial pelo total.</summary>
    public void Update(MessageDeltaUsage usage)
    {
        Output += usage.OutputTokens - _streamOutput;
        _streamOutput = usage.OutputTokens;
    }
}

/// <summary>Grava o consumo de cada pedido à IA e registra uma linha no log. Nunca derruba a resposta.</summary>
public sealed class AssistantUsageLog(IServiceScopeFactory scopes, TimeProvider time, ILogger<AssistantUsageLog> logger)
{
    public async Task RecordAsync(AssistantUsageTag tag, string model, UsageMeter meter)
    {
        logger.LogInformation(
            "Consumo da IA ({Kind}, {Model}, {Calls} chamada(s)): {Input} tokens de entrada, {Read} lidos do cache, {Written} gravados no cache, {Output} de saída.",
            tag.Kind, model, meter.Calls, meter.Input, meter.CacheRead, meter.CacheWrite, meter.Output);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            db.AssistantUsage.Add(new AssistantUsage
            {
                At = time.GetUtcNow().UtcDateTime,
                Kind = tag.Kind,
                DeviceId = tag.DeviceId,
                QuestionId = tag.QuestionId,
                Model = model,
                Calls = meter.Calls,
                InputTokens = meter.Input,
                CacheReadTokens = meter.CacheRead,
                CacheWriteTokens = meter.CacheWrite,
                OutputTokens = meter.Output,
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível gravar o consumo da IA.");
        }
    }
}
