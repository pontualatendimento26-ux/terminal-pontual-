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
    /// <summary>Versão do Terminal informada no último heartbeat (vazio nas versões que não informam).</summary>
    public string TerminalVersion { get; set; } = "";
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

/// <summary>
/// Domínio que uma atendente abriu no Google Chrome e que não faz parte dos sites operacionais dela.
/// O TI decide na Central: liberar (o Terminal para de avisar) ou bloquear (o Chrome recusa o site).
/// A decisão vale para todos os computadores e também para os subdomínios.
/// </summary>
public sealed class ChromeSite
{
    public const string StatusPending = "pending";
    public const string StatusAllowed = "allowed";
    public const string StatusBlocked = "blocked";

    public Guid Id { get; set; }
    public required string Host { get; set; }
    public string Status { get; set; } = StatusPending;
    /// <summary>Último computador que abriu o domínio.</summary>
    public Guid DeviceId { get; set; }
    /// <summary>Quantas vezes o domínio apareceu na tela (cada volta depois de alguns minutos fora conta de novo).</summary>
    public int Visits { get; set; }
    /// <summary>Minutos, aproximados, em que o domínio ficou aberto na aba visível do Chrome.</summary>
    public int Minutes { get; set; }
    public DateTime FirstAt { get; set; }
    public DateTime LastAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
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
    /// <summary>"0" desliga a análise automática das conversas (perfil das atendentes e sugestões); o padrão é ligada.</summary>
    public const string AssistantAutoAnalysis = "assistant.autoAnalysis";
    /// <summary>"0" impede a assistente de receber a tela e o WhatsApp da atendente junto com a pergunta; o padrão é ligado.</summary>
    public const string AssistantScreen = "assistant.screen";
    /// <summary>"0" impede o Terminal de registrar as conversas do WhatsApp para a análise; o padrão é ligado.</summary>
    public const string AssistantWhatsAppLearning = "assistant.whatsappLearning";
    /// <summary>Perfis montados a partir de históricos de conversas importados: objeto JSON nome da atendente → perfil.</summary>
    public const string AssistantImportedProfiles = "assistant.importedProfiles";

    public required string Key { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>Tipos de conhecimento da assistente. Todos ficam na mesma tabela e chegam à IA pelo mesmo caminho.</summary>
public static class KnowledgeKind
{
    public const string Script = "script";
    public const string Document = "document";
    public const string Knowledge = "knowledge";

    public static readonly string[] All = [Script, Document, Knowledge];

    public static string Label(string kind) => kind switch
    {
        Document => "documento",
        Knowledge => "conhecimento",
        _ => "roteiro",
    };
}

/// <summary>Situação do arquivo de um documento enviado.</summary>
public static class FileStatus
{
    public const string None = "";
    public const string Processed = "processed";
    /// <summary>PDF sem texto (escaneado): fica guardado à espera do leitor de PDF escaneado.</summary>
    public const string NoText = "no_text";
    public const string Error = "error";
}

/// <summary>
/// Um conhecimento da assistente: roteiro operacional, documento enviado ou conhecimento operacional avulso.
/// (O nome da classe e da tabela vem da primeira versão, que só tinha roteiros.)
/// </summary>
public sealed class AssistantScript
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
    public string UpdatedByName { get; set; } = "";

    public string Kind { get; set; } = KnowledgeKind.Script;
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>Banco ou assunto a que o conhecimento se refere.</summary>
    public string Bank { get; set; } = "";
    public string Keywords { get; set; } = "";
    /// <summary>Como a IA deve usar este conhecimento; vai junto com o conteúdo.</summary>
    public string AiInstructions { get; set; } = "";
    /// <summary>Observações internas da Pontual; não vão para a IA.</summary>
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>De onde veio: "manual", "arquivo", "atendentes" (sugestão aprovada) ou "sistema".</summary>
    public string Source { get; set; } = "manual";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string FileStatus { get; set; } = "";
    public string FileError { get; set; } = "";
}

/// <summary>Ligação entre dois conhecimentos (ex.: um roteiro e o documento do banco). Não tem direção.</summary>
public sealed class KnowledgeLink
{
    public Guid Id { get; set; }
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class SuggestionStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

public static class SuggestionKind
{
    /// <summary>A atendente marcou uma resposta como errada e escreveu a correção.</summary>
    public const string Correction = "correction";
    /// <summary>Assunto perguntado para o qual a base não tem resposta.</summary>
    public const string Gap = "gap";
    /// <summary>Informação nova que uma atendente contou na conversa.</summary>
    public const string NewInfo = "new_info";
    /// <summary>Conteúdo existente que parece errado, desatualizado ou confuso.</summary>
    public const string Review = "review";

    public static readonly string[] All = [Correction, Gap, NewInfo, Review];
}

/// <summary>
/// Sugestão de conhecimento vinda das atendentes (pelo botão do chat) ou da análise das conversas.
/// Nada daqui chega à IA antes de um administrador aprovar.
/// </summary>
public sealed class KnowledgeSuggestion
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>Rascunho do texto que viraria conhecimento.</summary>
    public string ProposedContent { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>"attendant" (botão do chat) ou "analysis" (análise das conversas pela IA).</summary>
    public string Source { get; set; } = "analysis";
    public Guid? DeviceId { get; set; }
    /// <summary>Resposta da assistente avaliada pela atendente (só nas correções vindas do chat).</summary>
    public long? MessageId { get; set; }
    public string AttendantName { get; set; } = "";
    /// <summary>Pergunta de exemplo que originou a sugestão.</summary>
    public string Question { get; set; } = "";
    /// <summary>Resposta da assistente que foi corrigida (só nas correções).</summary>
    public string Answer { get; set; } = "";
    /// <summary>Quantas vezes o assunto apareceu nas conversas.</summary>
    public int Occurrences { get; set; } = 1;
    public string Status { get; set; } = SuggestionStatus.Pending;
    /// <summary>Conhecimento a que a sugestão foi vinculada, ou que nasceu dela.</summary>
    public Guid? ItemId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string ReviewedByName { get; set; } = "";
}

/// <summary>Avaliação que a atendente deu a uma resposta da assistente.</summary>
public sealed class AssistantFeedback
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public long MessageId { get; set; }
    public bool Helpful { get; set; }
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
}

/// <summary>Perfil de atendimento de uma atendente, montado pela IA a partir das conversas dela com a assistente.</summary>
public sealed class AttendantInsight
{
    public Guid DeviceId { get; set; }
    /// <summary>JSON do perfil (ver <c>AssistantAnalysis</c>).</summary>
    public string ProfileJson { get; set; } = "";
    /// <summary>Última fala já analisada: a próxima análise lê só o que veio depois.</summary>
    public long AnalyzedThroughMessageId { get; set; }
    public int MessagesAnalyzed { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Model { get; set; } = "";
    public string LastError { get; set; } = "";
    /// <summary>Quantas conversas de WhatsApp já entraram na análise.</summary>
    public int WhatsAppAnalyzed { get; set; }
}

/// <summary>
/// Conversa de WhatsApp de uma atendente com um cliente, registrada pelo Terminal para a análise.
/// É uma fila: depois que a IA analisa a conversa, a linha é apagada (ficam só o perfil e as sugestões).
/// O que não for analisado em 7 dias também é apagado.
/// </summary>
public sealed class WhatsAppCapture
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>Nome do contato como aparece no WhatsApp (ou o telefone).</summary>
    public required string Contact { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Modo da cobertura de ausência de uma atendente.</summary>
public static class CoverageMode
{
    public const string Off = "";
    /// <summary>Ensaio: a assistente lê as mensagens novas e escreve a resposta, mas nada é enviado ao cliente.</summary>
    public const string Test = "test";
    /// <summary>Ligada: envia sozinha o que é simples; o que envolve dinheiro espera aprovação.</summary>
    public const string On = "on";

    public static readonly string[] All = [Off, Test, On];
}

/// <summary>Situação de uma resposta da cobertura.</summary>
public static class CoverageStatus
{
    /// <summary>Escrita no modo ensaio; não vai ao cliente.</summary>
    public const string Draft = "draft";
    /// <summary>Envolve dinheiro ou algo sensível: espera uma pessoa aprovar.</summary>
    public const string Pending = "pending";
    /// <summary>Aprovada na Central; o Terminal envia na próxima volta.</summary>
    public const string Approved = "approved";
    /// <summary>A assistente decidiu enviar; o Terminal está enviando.</summary>
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    /// <summary>Não pedia resposta (um "ok", uma figurinha).</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// Uma mensagem de cliente tratada pela cobertura de ausência: o que o cliente escreveu, o que a assistente
/// respondeu (ou propôs) e o que aconteceu. Fica 30 dias para a supervisão conferir; depois é apagada.
/// </summary>
public sealed class CoverageReply
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public required string Contact { get; set; }
    /// <summary>As últimas mensagens do cliente que pediram resposta.</summary>
    public string Incoming { get; set; } = "";
    /// <summary>A conversa como estava na tela, para quem for aprovar entender o contexto.</summary>
    public string Transcript { get; set; } = "";
    /// <summary>Resposta escrita pela assistente (ou corrigida por quem aprovou).</summary>
    public string Reply { get; set; } = "";
    /// <summary>Aviso curto enviado na hora enquanto a resposta espera aprovação ("vou verificar e já te retorno").</summary>
    public string Holding { get; set; } = "";
    /// <summary>"send", "queue" ou "none": o que a assistente decidiu.</summary>
    public string Decision { get; set; } = "";
    public string Reason { get; set; } = "";
    public required string Status { get; set; }
    /// <summary>Marca das mensagens do cliente já tratadas, para não responder duas vezes a mesma coisa.</summary>
    public string Fingerprint { get; set; } = "";
    public string Error { get; set; } = "";
    public string ReviewedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

/// <summary>Perfil da assistente de uma atendente (uma máquina = uma atendente, ADR 0003).</summary>
public sealed class AssistantProfile
{
    public Guid DeviceId { get; set; }
    /// <summary>Orientações da supervisão só para esta atendente.</summary>
    public string Instructions { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Cobertura da ausência: a assistente responde os clientes desta atendente no WhatsApp (ver <see cref="CoverageMode"/>).</summary>
    public string Coverage { get; set; } = "";
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
    /// <summary>
    /// Nomes do que estava aberto na tela quando a pergunta foi feita (ex.: "WhatsApp, Safra").
    /// O conteúdo da tela vai só para a IA, naquela pergunta, e não é guardado aqui.
    /// </summary>
    public string Context { get; set; } = "";
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
