namespace Pontual.Terminal.Ipc;

/// <summary>Um site ou programa liberado para a máquina (docs/V1-CONTRATO.md, seção 5).</summary>
public sealed record ConfigItem(string Id, string Name, string Kind, string Target, string Group, IReadOnlyList<string> Hosts)
{
    public const string KindSite = "site";
    public const string KindProgram = "program";

    public bool IsProgram => string.Equals(Kind, KindProgram, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resposta de <c>GET /device/config</c>; também é o formato do arquivo <c>config-local.json</c>.</summary>
public sealed record TerminalConfig(int Version, string? AttendantName, bool ObservationMode, IReadOnlyList<ConfigItem> Items)
{
    public ConfigItem? Find(string id) => Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
}

public sealed record MaintenanceInfo(string? Reason, string? Until);

/// <summary>Resposta de <c>POST /device/heartbeat</c>.</summary>
public sealed record HeartbeatResponse(string State, int ConfigVersion, string? AttendantName, MaintenanceInfo? Maintenance, bool ObservationMode)
{
    public const string StatePending = "pending";
    public const string StateActive = "active";
    public const string StateMaintenance = "maintenance";
}

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
}
