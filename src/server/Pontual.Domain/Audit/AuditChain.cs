using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pontual.Domain.Audit;

/// <summary>
/// Encadeamento por hash do registro de auditoria (docs/SECURITY.md, seção 7).
/// </summary>
/// <remarks>
/// <para>
/// Garante adulteração detectável no meio da cadeia: alterar, remover, inserir ou reordenar registros.
/// </para>
/// <para>
/// NÃO detecta truncamento do final: uma cadeia da qual se removeram os últimos registros continua
/// íntegra por si só. Também não detecta a reescrita completa da cadeia por quem recalcula todos os
/// hashes. Os dois casos dependem da âncora externa (audit_anchors), tratada em outra camada.
/// </para>
/// <para>
/// Formato canônico (v1), na ordem: rótulo de versão, sequência, data, tipo de ator, id do ator, ação,
/// tipo do alvo, id do alvo, justificativa, hash anterior. Texto e bytes levam prefixo de comprimento
/// (int32 big-endian, contado em bytes UTF-8); campos opcionais levam um byte de presença (0 ou 1).
/// Assim nenhum conteúdo pode "escorrer" de um campo para o vizinho, e nulo difere de vazio.
/// Mudar qualquer detalhe deste formato invalida as cadeias já gravadas.
/// </para>
/// </remarks>
public static class AuditChain
{
    public const int HashSize = SHA256.HashSizeInBytes;

    private const string FormatLabel = "pontual.audit.v1";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    // Estrito: um surrogate solto seria trocado por U+FFFD e textos diferentes teriam o mesmo hash.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly ImmutableArray<byte> GenesisPreviousHash = [.. new byte[HashSize]];

    /// <summary>Cria o registro seguinte a <paramref name="previous"/>, ou o primeiro da cadeia se for nulo.</summary>
    /// <remarks>Não verifica <paramref name="previous"/>; quem chama deve passar a cabeça de uma cadeia confiável.</remarks>
    public static AuditEntry Append(AuditEntry? previous, AuditEntryData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(data.Action, nameof(data));
        if (!Enum.IsDefined(data.ActorType))
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.ActorType, "Tipo de ator desconhecido.");
        }

        var entry = new AuditEntry(
            Sequence: checked((previous?.Sequence ?? 0) + 1),
            OccurredAt: Normalize(data.OccurredAt),
            data.ActorType,
            data.ActorId,
            data.Action,
            data.TargetType,
            data.TargetId,
            data.Justification,
            PreviousHash: previous?.Hash ?? GenesisPreviousHash,
            Hash: default);

        return entry with { Hash = ComputeHash(entry) };
    }

    /// <summary>
    /// Percorre os registros na ordem recebida, desde o primeiro da cadeia, e para na primeira falha.
    /// Em cada registro confere, nesta ordem: sequência, elo com o anterior, hash do conteúdo.
    /// </summary>
    public static AuditChainVerification Verify(IEnumerable<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        long expectedSequence = 1;
        var expectedPreviousHash = GenesisPreviousHash;

        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(entries));

            if (entry.Sequence != expectedSequence)
            {
                return AuditChainVerification.Broken(entry.Sequence, AuditChainFailure.SequenceGap);
            }

            if (!FixedTimeEquals(entry.PreviousHash, expectedPreviousHash))
            {
                return AuditChainVerification.Broken(entry.Sequence, AuditChainFailure.PreviousHashMismatch);
            }

            if (!HashMatches(entry))
            {
                return AuditChainVerification.Broken(entry.Sequence, AuditChainFailure.HashMismatch);
            }

            expectedSequence = entry.Sequence + 1;
            expectedPreviousHash = entry.Hash;
        }

        return AuditChainVerification.Intact;
    }

    // O banco guarda timestamptz: instante em UTC com precisão de microssegundo. Normalizar aqui faz o
    // registro devolvido ser idêntico ao que será relido, senão o hash deixaria de conferir após a gravação.
    private static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static bool HashMatches(AuditEntry entry)
    {
        try
        {
            return FixedTimeEquals(entry.Hash, ComputeHash(entry));
        }
        catch (EncoderFallbackException)
        {
            // Texto que Append nunca teria aceitado: o registro não saiu desta cadeia.
            return false;
        }
    }

    private static bool FixedTimeEquals(ImmutableArray<byte> left, ImmutableArray<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left.AsSpan(), right.AsSpan());

    private static ImmutableArray<byte> ComputeHash(AuditEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        WriteText(buffer, FormatLabel);
        WriteInt64(buffer, entry.Sequence);
        WriteText(buffer, entry.OccurredAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        WriteText(buffer, ActorTypeCode(entry.ActorType));
        WriteOptionalGuid(buffer, entry.ActorId);
        WriteText(buffer, entry.Action);
        WriteOptionalText(buffer, entry.TargetType);
        WriteOptionalGuid(buffer, entry.TargetId);
        WriteOptionalText(buffer, entry.Justification);
        WriteBytes(buffer, entry.PreviousHash.AsSpan());

        return [.. SHA256.HashData(buffer.WrittenSpan)];
    }

    // Texto fixo (o mesmo da coluna actor_type) em vez do valor numérico: reordenar o enum não muda hashes.
    // Um valor fora do enum vira seu número, que nunca coincide com um dos nomes.
    private static string ActorTypeCode(ActorType actorType) => actorType switch
    {
        ActorType.Admin => "admin",
        ActorType.Attendant => "attendant",
        ActorType.Device => "device",
        ActorType.System => "system",
        _ => ((int)actorType).ToString(CultureInfo.InvariantCulture),
    };

    private static void WriteInt32(ArrayBufferWriter<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.GetSpan(sizeof(int)), value);
        buffer.Advance(sizeof(int));
    }

    private static void WriteInt64(ArrayBufferWriter<byte> buffer, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(buffer.GetSpan(sizeof(long)), value);
        buffer.Advance(sizeof(long));
    }

    private static void WriteBytes(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
    {
        WriteInt32(buffer, value.Length);
        buffer.Write(value);
    }

    private static void WriteText(ArrayBufferWriter<byte> buffer, string value)
    {
        var length = StrictUtf8.GetByteCount(value);
        WriteInt32(buffer, length);
        StrictUtf8.GetBytes(value, buffer.GetSpan(length));
        buffer.Advance(length);
    }

    private static void WritePresence(ArrayBufferWriter<byte> buffer, bool present)
    {
        buffer.GetSpan(1)[0] = present ? (byte)1 : (byte)0;
        buffer.Advance(1);
    }

    private static void WriteOptionalText(ArrayBufferWriter<byte> buffer, string? value)
    {
        WritePresence(buffer, value is not null);
        if (value is not null)
        {
            WriteText(buffer, value);
        }
    }

    private static void WriteOptionalGuid(ArrayBufferWriter<byte> buffer, Guid? value)
    {
        WritePresence(buffer, value.HasValue);
        if (value is { } guid)
        {
            // Ordem dos bytes fixa (RFC 9562), independente da arquitetura.
            guid.TryWriteBytes(buffer.GetSpan(16), bigEndian: true, out var written);
            buffer.Advance(written);
        }
    }
}
