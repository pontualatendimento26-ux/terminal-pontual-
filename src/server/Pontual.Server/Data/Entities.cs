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
    /// <summary>O que a cobertura de ausência está fazendo neste Terminal, como ele contou no último sinal.</summary>
    public string CoverageStatus { get; set; } = "";
    public DateTime? CoverageStatusAt { get; set; }
    /// <summary>SHA-256 (hex) do token; o token em si nunca é gravado.</summary>
    public required string TokenHash { get; set; }
    public string? AttendantName { get; set; }
    public bool Approved { get; set; }
    public bool ObservationMode { get; set; } = true;
    /// <summary>
    /// Aplicativos da Microsoft Store neste computador: o Terminal mantém o Explorer rodando escondido, que o Windows exige
    /// para abri-los. Começa desligado; o TI liga computador por computador.
    /// </summary>
    public bool StoreApps { get; set; }
    /// <summary>
    /// Modo leve, para computador fraco: gravação menor, abas paradas em economia e vigias mais espaçadas.
    /// Começa desligado; o TI liga computador por computador.
    /// </summary>
    public bool LightMode { get; set; }
    /// <summary>
    /// Cofre de senhas do navegador interno, para um Terminal dedicado a uma atendente: o WebView2 volta a oferecer
    /// guardar e preencher a senha nos sites (como o Chrome), no perfil próprio do Terminal. Começa desligado; o TI liga computador por computador.
    /// </summary>
    public bool SavePasswords { get; set; }
    /// <summary>
    /// Papel de parede da tela inicial: nome do arquivo na pasta <c>papeis-de-parede</c> (ao lado do banco), ou vazio.
    /// O TI envia a imagem no painel do computador; a tela inicial busca com o token da máquina.
    /// </summary>
    public string Wallpaper { get; set; } = "";
    /// <summary>Papel de parede repetido como azulejo (para padrões). Desligado = uma imagem só, cobrindo a tela.</summary>
    public bool WallpaperTile { get; set; }
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

/// <summary>
/// Domínio aberto por UMA atendente (um computador) num navegador, com a decisão só para ela. A decisão dela vale
/// mais que a decisão para todas (<see cref="ChromeSite"/>): o YouTube pode estar bloqueado para todas e liberado
/// só para uma. Os sites novos esperam decisão aqui, um por atendente.
/// </summary>
public sealed class ChromeDeviceSite
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public required string Host { get; set; }
    public string Status { get; set; } = ChromeSite.StatusPending;
    /// <summary>Último navegador em que ela abriu o domínio ("Mozilla Firefox").</summary>
    public string Browser { get; set; } = "";
    public int Visits { get; set; }
    public int Minutes { get; set; }
    public DateTime FirstAt { get; set; }
    public DateTime LastAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
}

/// <summary>
/// Aplicativo do computador (Windows, instalado ou baixado) que uma atendente abriu pela lista "Aplicativos" do Terminal.
/// A chave é o nome do arquivo (<c>spotify.exe</c>), porque o caminho muda de um computador para outro. Esta é a decisão
/// para TODAS as atendentes; a de uma só fica em <see cref="DeviceApp"/> e vale mais.
/// </summary>
public sealed class AppDecision
{
    public const string StatusPending = "pending";
    public const string StatusAllowed = "allowed";
    public const string StatusBlocked = "blocked";

    public Guid Id { get; set; }
    /// <summary>Nome do arquivo em minúsculas, com a extensão.</summary>
    public required string Exe { get; set; }
    /// <summary>Nome que a atendente vê ("Spotify"), o último informado por um Terminal.</summary>
    public string Name { get; set; } = "";
    public string Status { get; set; } = StatusPending;
    public DateTime FirstAt { get; set; }
    public DateTime LastAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
}

/// <summary>
/// Aplicativo aberto por UMA atendente (um computador), com o pedido esperando decisão ou a decisão só para ela.
/// A decisão dela vale mais que a decisão para todas (<see cref="AppDecision"/>).
/// </summary>
public sealed class DeviceApp
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public required string Exe { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Caminho completo do .exe no computador dela, o último informado.</summary>
    public string Path { get; set; } = "";
    public string Status { get; set; } = AppDecision.StatusPending;
    /// <summary>Quantas vezes ela abriu (ou tentou abrir) o aplicativo.</summary>
    public int Opens { get; set; }
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
    /// <summary>Nomes (um por linha) dos arquivos da pasta <c>sistemas-iniciais</c> já carregados na base.</summary>
    public const string AssistantSystemsSeeded = "assistant.systemsSeeded";
    /// <summary>Nomes (um por linha) dos arquivos da pasta <c>convenios-iniciais</c> já carregados na base.</summary>
    public const string AssistantAgreementsSeeded = "assistant.agreementsSeeded";

    /// <summary>Marca da limpeza única que tirou o site "Teams" (fica só o aplicativo Microsoft Teams).</summary>
    public const string TeamsSiteRemoved = "items.teamsSiteRemoved";
    /// <summary>"0" desliga a análise automática das conversas (perfil das atendentes e sugestões); o padrão é ligada.</summary>
    public const string AssistantAutoAnalysis = "assistant.autoAnalysis";
    /// <summary>"0" impede a assistente de receber a tela e o WhatsApp da atendente junto com a pergunta; o padrão é ligado.</summary>
    public const string AssistantScreen = "assistant.screen";
    /// <summary>"0" impede o Terminal de registrar as conversas do WhatsApp para a análise; o padrão é ligado.</summary>
    public const string AssistantWhatsAppLearning = "assistant.whatsappLearning";
    /// <summary>Perfis montados a partir de históricos de conversas importados: objeto JSON nome da atendente → perfil.</summary>
    public const string AssistantImportedProfiles = "assistant.importedProfiles";

    /// <summary>"0" desliga a gravação da tela das atendentes; o padrão é ligada.</summary>
    public const string RecordingEnabled = "recording.enabled";
    /// <summary>Chave AES-256 dos vídeos gravados, protegida pelo Windows (DPAPI da máquina).</summary>
    public const string RecordingKey = "recording.key";

    /// <summary>Endereço do CRM da Pontual (sistema interno), ex.: http://192.168.0.243:3000.</summary>
    public const string CrmUrl = "crm.url";
    /// <summary>Login da conta do CRM escolhida como Agente Externo do funil.</summary>
    public const string CrmUser = "crm.user";
    /// <summary>Senha dessa conta, cifrada como a chave da IA (ver <c>AssistantSecrets</c>).</summary>
    public const string CrmPassword = "crm.password";
    /// <summary>Segredo que vai no endereço do webhook; aviso sem ele é recusado.</summary>
    public const string CrmWebhookKey = "crm.webhookKey";
    /// <summary>"1" liga a IA nos clientes do CRM (ela escreve as respostas para aprovação); o padrão é desligada.</summary>
    public const string CrmEnabled = "crm.enabled";
    /// <summary>"1" liga a busca dos clientes perdidos (ver <c>CrmLost</c>); o padrão é desligada.</summary>
    public const string CrmLostEnabled = "crm.lostEnabled";
    /// <summary>O período escolhido na tela ("2026-11-01|2026-11-20", dias de Brasília); vazio quando não há.</summary>
    public const string CrmLostPeriod = "crm.lostPeriod";
    /// <summary>Quando a busca dos perdidos rodou por último e o que achou, para a tela.</summary>
    public const string CrmLostLastRun = "crm.lostLastRun";
    /// <summary>Endereço da API de integração do Nexus, ex.: http://192.168.0.171:3001/api/integracao/v1.</summary>
    public const string NexusUrl = "nexus.url";
    /// <summary>Token da Central no Nexus, cifrado como a chave da IA (ver <c>AssistantSecrets</c>).</summary>
    public const string NexusToken = "nexus.token";

    // Margem liberada do SIAPE (ver <c>SiapeMargin</c>): a base do SQL Server na máquina da Kamila.
    /// <summary>Servidor SQL, no formato IP,porta (ex.: 192.168.0.160,1433).</summary>
    public const string SiapeServer = "siape.server";
    public const string SiapeDatabase = "siape.database";
    public const string SiapeUser = "siape.user";
    /// <summary>Senha do login SQL, cifrada como a chave da IA.</summary>
    public const string SiapePassword = "siape.password";
    /// <summary>"1" liga a busca mensal; o padrão é desligada.</summary>
    public const string SiapeEnabled = "siape.enabled";
    /// <summary>"1" manda a mensagem ao cliente sem passar pela aprovação; o padrão é esperar aprovação.</summary>
    public const string SiapeAutoSend = "siape.autoSend";
    /// <summary>Dia do mês a partir do qual a busca roda (1 a 28).</summary>
    public const string SiapeDay = "siape.day";
    /// <summary>Quantas mensagens no máximo por mês.</summary>
    public const string SiapeMax = "siape.max";
    /// <summary>Valor liberado total mínimo (R$, coluna TOTAL_LIBERADO) para o servidor entrar; o dono pediu R$ 1.000.</summary>
    public const string SiapeMinLiberado = "siape.minLiberado";
    /// <summary>Nome da tabela do mês; vazio = automático (a mais nova com telefone e valor liberado).</summary>
    public const string SiapeTable = "siape.table";
    /// <summary>A linha de WhatsApp do CRM (bot_id) por onde as mensagens saem.</summary>
    public const string SiapeBotId = "siape.botId";
    /// <summary>Nome da atendente cujo jeito de escrever a IA imita; vazio = jeito geral da equipe.</summary>
    public const string SiapeAttendant = "siape.attendant";
    /// <summary>Quando a busca rodou por último e o que achou, para a tela.</summary>
    public const string SiapeLastRun = "siape.lastRun";
    /// <summary>"2026-12": o mês (de Brasília) em que a busca automática já rodou.</summary>
    public const string SiapeLastMonth = "siape.lastMonth";

    public required string Key { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>Tipos de conhecimento da assistente. Todos ficam na mesma tabela e chegam à IA pelo mesmo caminho.</summary>
public static class KnowledgeKind
{
    public const string Script = "script";
    public const string Document = "document";
    public const string Knowledge = "knowledge";
    /// <summary>Como funcionam os sistemas da Pontual (Terminal, Central, IA...): resumos que a IA usa para explicar e orientar.</summary>
    public const string System = "system";

    public static readonly string[] All = [Script, Document, Knowledge, System];

    /// <summary>Tipos escritos à mão na Central Neural (documentos entram só por arquivo).</summary>
    public static bool IsWritable(string? kind) => kind is Script or Knowledge or System;

    public static string Label(string kind) => kind switch
    {
        Document => "documento",
        Knowledge => "conhecimento",
        System => "aprendizado sobre os sistemas da Pontual",
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

    /// <summary>
    /// Só para as atendentes (ex.: a planilha de logins e senhas): a IA do chat usa, mas a IA que responde os
    /// clientes na cobertura de ausência nunca recebe este conhecimento.
    /// </summary>
    public bool StaffOnly { get; set; }

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

    /// <summary>UF do convênio (GO, MT, MS, TO, DF...). Vazio quando o conhecimento não é de um convênio.</summary>
    public string Uf { get; set; } = "";
    /// <summary>
    /// Posição de venda dentro do banco: 1 = o roteiro que mais vende, 2 o seguinte... 0 = sem ranking. Marcado à
    /// mão pela gestão na Central Neural; é o que a IA usa quando a atendente pergunta "qual o melhor roteiro".
    /// </summary>
    public int SalesRank { get; set; }
    /// <summary>De onde veio o ranking (ex.: "Nov/2026: 32 propostas pagas"). Vai para a IA junto do ranking.</summary>
    public string SalesNote { get; set; } = "";
    /// <summary>
    /// Dados estruturados do convênio (prazo, margem, averbadora, idade x valor, arquivo no portal...), em JSON
    /// de chave → texto. A Central Neural monta os cartões com eles; a IA lê a versão em texto no conteúdo.
    /// </summary>
    public string DetailsJson { get; set; } = "";
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
    /// <summary>Último aviso do CRM já analisado: a próxima análise lê só o que a atendente escreveu depois.</summary>
    public long CrmAnalyzedThroughEventId { get; set; }
    /// <summary>Quantas conversas do CRM já entraram na análise.</summary>
    public int CrmAnalyzed { get; set; }
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
    /// <summary>
    /// Dias e horários em que a cobertura atende por esta atendente: sete trechos separados por ";", de domingo
    /// a sábado, cada um "HH:mm-HH:mm" ou vazio (não atende no dia). Vazio = segunda a sexta, das 8h às 18h.
    /// </summary>
    public string CoverageSchedule { get; set; } = "";
    /// <summary>
    /// Dia (yyyy-MM-dd, hora de Brasília) em que a supervisão marcou que a atendente faltou. Nesse dia a cobertura
    /// fica ligada e responde na hora, sem esperar o computador ficar parado. Vale só para o dia marcado.
    /// </summary>
    public string AbsentOn { get; set; } = "";
    /// <summary>
    /// Números das linhas de WhatsApp desta atendente no CRM, só dígitos, separados por vírgula. No dia da falta,
    /// a IA escreve a resposta (para aprovação) aos clientes que escreverem nessas linhas.
    /// </summary>
    public string CrmLines { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Um texto que a atendente salvou no perfil dela e chama por uma palavra-chave.</summary>
public sealed class SavedText
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>A palavra-chave na forma de comparação (sem acento nem maiúscula).</summary>
    public required string Keyword { get; set; }
    /// <summary>A palavra-chave como a atendente escreveu.</summary>
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
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

/// <summary>Para que a IA foi chamada; aparece no consumo da Central.</summary>
public static class UsageKind
{
    public const string Question = "pergunta";
    public const string Test = "teste";
    public const string Coverage = "cobertura";
    public const string Crm = "crm";
    public const string Analysis = "analise";
    public const string Other = "outro";
}

/// <summary>
/// Consumo de um pedido à IA, como a própria API informou. Uma pergunta no modo índice faz várias
/// chamadas (consulta à base, resposta) e vira uma linha só, com a soma.
/// O que se paga por token: entrada normal 1×, gravação do cache de 1 hora 2×, leitura do cache 0,1×.
/// </summary>
public sealed class AssistantUsage
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public required string Kind { get; set; }
    public Guid? DeviceId { get; set; }
    /// <summary>A fala da atendente (<see cref="AssistantMessage"/>) que gerou o consumo, quando é uma pergunta.</summary>
    public long? QuestionId { get; set; }
    public required string Model { get; set; }
    /// <summary>Quantas chamadas à API o pedido fez.</summary>
    public int Calls { get; set; }
    /// <summary>Tokens de entrada cobrados por inteiro (o que veio depois do ponto de cache).</summary>
    public long InputTokens { get; set; }
    /// <summary>Tokens lidos do cache: se ficar em zero pergunta após pergunta, o cache não está pegando.</summary>
    public long CacheReadTokens { get; set; }
    /// <summary>Tokens gravados no cache (a primeira pergunta depois que ele expira ou a base muda).</summary>
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }
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

/// <summary>
/// Um trecho (cerca de 1 minuto) da gravação da tela de um computador. O vídeo fica cifrado num arquivo fora do banco
/// (<c>RecordingStore</c>); aqui só o índice. Apagado com o arquivo depois de 3 meses.
/// </summary>
public sealed class ScreenRecording
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>Nome da atendente na hora da gravação (a máquina pode trocar de atendente depois).</summary>
    public string AttendantName { get; set; } = "";
    /// <summary>Início do trecho, em UTC, pelo relógio do computador.</summary>
    public DateTime StartedAt { get; set; }
    public double Seconds { get; set; }
    public long Bytes { get; set; }
    /// <summary>Caminho relativo à pasta das gravações.</summary>
    public string File { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    /// <summary>
    /// Vídeo do dia: JSON com os trechos gravados, [[início em segundos desde o começo do vídeo, duração], ...].
    /// Entre um trecho e outro o computador estava desligado. Nulo = trecho único de 1 minuto (Terminal 2.14.0 e 2.14.1).
    /// </summary>
    public string? Parts { get; set; }
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

/// <summary>Um aviso do CRM recebido pelo webhook do Agente Externo, com o que foi possível ler dele e o corpo como veio.</summary>
/// <summary>Situação de uma consulta de CPF no Nexus.</summary>
public static class NexusStatus
{
    public const string Running = "em_andamento";
    public const string Done = "concluida";
    public const string NotFound = "nao_encontrado";
    public const string Failed = "erro";
}

/// <summary>
/// Uma consulta de CPF que a atendente pediu no chat da IA. O resultado fica guardado 24 horas: o mesmo CPF no mesmo
/// setor, nesse prazo, é respondido daqui, sem chamar o Nexus. Depois disso o resultado é apagado e fica só o registro
/// (quem pediu, CPF, setor, se foi paga).
/// </summary>
public sealed class NexusConsulta
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string AttendantName { get; set; } = "";
    public string Cpf { get; set; } = "";
    /// <summary>siape, inss ou exercito.</summary>
    public string Setor { get; set; } = "";
    /// <summary>Ver <see cref="NexusStatus"/>.</summary>
    public string Status { get; set; } = "";
    /// <summary>O consulta_id do Nexus.</summary>
    public string NexusId { get; set; } = "";
    /// <summary>Esta consulta custou uma consulta paga no Nexus.</summary>
    public bool Paga { get; set; }
    /// <summary>O Nexus respondeu do cache dele (nada foi cobrado).</summary>
    public bool FromCache { get; set; }
    /// <summary>A resposta do Nexus como veio; apagada depois de 24 horas.</summary>
    public string ResultJson { get; set; } = "";
    /// <summary>O texto que apareceu no chat; apagado junto com o resultado.</summary>
    public string Summary { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>A conversa do chat em que o resultado vai aparecer.</summary>
    public Guid ConversationId { get; set; }
    /// <summary>A fala do chat com o resultado, quando a consulta terminou.</summary>
    public long? MessageId { get; set; }
}

public sealed class CrmEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    /// <summary>ID da conversa no CRM (conversa.id); vazio se o aviso não trouxe.</summary>
    public string ConversationId { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>Quem escreveu: <see cref="CrmAuthor"/>.</summary>
    public string Author { get; set; } = "";
    /// <summary>Nome de quem da equipe digitou (remetente.nome), como está cadastrado no CRM; vazio nas mensagens do cliente.</summary>
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Número da linha de WhatsApp da Pontual em que a conversa está (conversa.linha.numero), só dígitos.</summary>
    public string Line { get; set; } = "";
    /// <summary>Nome da linha no CRM (conversa.linha.nome), para a tela.</summary>
    public string LineName { get; set; } = "";
    /// <summary>ID do funil da conversa (funil.id).</summary>
    public string Funnel { get; set; } = "";
    /// <summary>O corpo como chegou (cortado), para conferir o formato do CRM.</summary>
    public string Body { get; set; } = "";
}

public static class CrmAuthor
{
    public const string Client = "cliente";
    public const string Team = "equipe";
}

/// <summary>Resposta que a IA escreveu para um cliente do CRM. Só vai ao cliente depois que alguém aprova na Central Neural.</summary>
public sealed class CrmReply
{
    public Guid Id { get; set; }
    public required string ConversationId { get; set; }
    public string Contact { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>As mensagens do cliente que pediram resposta.</summary>
    public string Incoming { get; set; } = "";
    /// <summary>A conversa como a IA leu.</summary>
    public string Transcript { get; set; } = "";
    public string Reply { get; set; } = "";
    /// <summary>"perdido": achado pela busca dos clientes sem resposta, e enviado pela tela de conversa do CRM. Vazio: veio pelo webhook do agente.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Cliente perdido: o chat_id e a linha (bot_id) no CRM.</summary>
    public string ChatId { get; set; } = "";
    public string BotId { get; set; } = "";
    /// <summary>Cliente perdido: a última mensagem dele que a resposta leva em conta. Se mudou no CRM, a resposta ficou velha.</summary>
    public string LastWppId { get; set; } = "";
    /// <summary>Cliente perdido: quando ele escreveu a última vez.</summary>
    public DateTime? WaitingSince { get; set; }
    /// <summary>O texto que a IA escreveu, antes de alguém editar: a diferença para o enviado é o que ela aprende.</summary>
    public string Draft { get; set; } = "";
    /// <summary>"responder", "humano" ou "nada": o que a IA sugeriu.</summary>
    public string Decision { get; set; } = "";
    public string Reason { get; set; } = "";
    /// <summary>A atendente de quem é este cliente (a que faltou, ou a última que escreveu a ele no CRM); vazio se não deu para saber.</summary>
    public string Attendant { get; set; } = "";
    public required string Status { get; set; }
    /// <summary>Último aviso do cliente que esta resposta leva em conta: um aviso mais novo deixa a resposta velha.</summary>
    public long LastEventId { get; set; }
    public string Error { get; set; } = "";
    public string ReviewedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public static class CrmReplyStatus
{
    /// <summary>Esperando alguém aprovar, editar, recusar ou passar para uma atendente.</summary>
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Rejected = "rejected";
    /// <summary>A conversa foi passada para uma atendente.</summary>
    public const string Human = "human";
    /// <summary>O envio ao CRM falhou; dá para tentar de novo.</summary>
    public const string Failed = "failed";
    /// <summary>Não pedia resposta (um "ok", uma figurinha).</summary>
    public const string Skipped = "skipped";
    /// <summary>O cliente escreveu de novo antes da aprovação; outra resposta tomou o lugar desta.</summary>
    public const string Outdated = "outdated";
}

/// <summary>
/// Uma nota adesiva da atendente (pedido do dono, 25/11/2026): como as Notas Autoadesivas do Windows, só que do Terminal.
/// Fica guardada na Central, ligada ao computador, para não se perder se o PC for formatado. O TI pode ler as notas e
/// colar um recado na tela da atendente (<see cref="FromAdminName"/> preenchido).
/// </summary>
public sealed class StickyNote
{
    /// <summary>Criado pelo Terminal (a nota nasce no computador, mesmo sem conexão) ou pela Central (recado).</summary>
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>Um dos nomes de <c>NoteContent.Colors</c>.</summary>
    public string Color { get; set; } = "yellow";
    /// <summary>O texto, em blocos JSON já validados por <c>NoteContent</c>; nunca HTML.</summary>
    public string Content { get; set; } = "[]";
    /// <summary>Posição e tamanho da janela da nota na tela da atendente; zero = o Terminal escolhe.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>A nota está aberta na tela (verdadeiro) ou só na lista de notas.</summary>
    public bool Open { get; set; }
    /// <summary>Quem do TI enviou o recado; vazio para as notas da própria atendente.</summary>
    public string FromAdminName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Recado que o TI retirou: fica um tempo marcado para o Terminal saber que deve tirar da tela.</summary>
    public DateTime? DeletedAt { get; set; }
}
