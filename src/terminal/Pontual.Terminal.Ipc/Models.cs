namespace Pontual.Terminal.Ipc;

/// <summary>Um site ou programa liberado para a máquina (docs/V1-CONTRATO.md, seção 5).</summary>
public sealed record ConfigItem(string Id, string Name, string Kind, string Target, string Group, IReadOnlyList<string> Hosts)
{
    public const string KindSite = "site";
    public const string KindProgram = "program";

    public bool IsProgram => string.Equals(Kind, KindProgram, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resposta de <c>GET /device/config</c>; também é o formato do arquivo <c>config-local.json</c>.</summary>
/// <param name="ChromeAllowed">Domínios que o TI liberou no Google Chrome: não geram aviso (servidor antigo não manda).</param>
/// <param name="ChromeBlocked">Domínios que o TI bloqueou no Google Chrome: vão para a política do Chrome desta conta.</param>
public sealed record TerminalConfig(int Version, string? AttendantName, bool ObservationMode, IReadOnlyList<ConfigItem> Items,
    IReadOnlyList<string>? ChromeAllowed = null, IReadOnlyList<string>? ChromeBlocked = null)
{
    public ConfigItem? Find(string id) => Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
}

public sealed record MaintenanceInfo(string? Reason, string? Until);

/// <summary>Resposta de <c>POST /device/heartbeat</c>.</summary>
/// <param name="Assistant">A assistente de IA está ligada para esta máquina (servidor antigo não manda: fica falso).</param>
public sealed record HeartbeatResponse(string State, int ConfigVersion, string? AttendantName, MaintenanceInfo? Maintenance, bool ObservationMode, bool Assistant = false, bool WhatsAppLearning = false, string? Coverage = null)
{
    public const string StatePending = "pending";
    public const string StateActive = "active";
    public const string StateMaintenance = "maintenance";
}

/// <summary>O que a cobertura de ausência deve fazer com uma conversa: "send" (enviar <see cref="Message"/>), "retry" ou "skip".</summary>
public sealed record CoverageAction(string Action, Guid? Id = null, string? Message = null, bool Holding = false, string? Reason = null)
{
    public const string Send = "send";
    public const string Retry = "retry";
}

/// <summary>Resposta da cobertura aprovada na Central, para o Terminal enviar.</summary>
public sealed record CoverageOutgoing(Guid Id, string Contact, string Message);

public sealed record EnrollResponse(string DeviceId, string Token);

/// <summary>O TI está com a visualização ao vivo desta tela aberta? <c>Viewer</c> é o nome de quem está vendo.</summary>
public sealed record ScreenRequest(bool Wanted, string? Viewer);

/// <summary>Um evento do terminal: enviado ao servidor ou, no modo local, gravado no registro.</summary>
public sealed record DeviceEvent(string Type, string? Item, string? Detail, DateTimeOffset At)
{
    public const string SessionStarted = "session_started";
    public const string Opened = "opened";
    public const string Closed = "closed";
    public const string Blocked = "blocked";
    public const string Observed = "observed";
    public const string Help = "help";
    /// <summary>Domínio não operacional apareceu na aba visível do Google Chrome (detail = domínio).</summary>
    public const string ChromeVisit = "chrome_visit";
    /// <summary>O mesmo domínio continua na tela do Chrome: um a cada minuto (detail = domínio).</summary>
    public const string ChromeStay = "chrome_stay";
}
