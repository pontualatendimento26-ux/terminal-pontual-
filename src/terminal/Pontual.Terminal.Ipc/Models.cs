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
/// <param name="AppsAllowed">Aplicativos do computador (nome do .exe) que o TI liberou: abrem sem pedido (servidor antigo não manda).</param>
/// <param name="AppsBlocked">Aplicativos do computador (nome do .exe) que o TI bloqueou: o Terminal não abre e fecha se estiver aberto.</param>
/// <param name="LightMode">Modo leve, para computador fraco (servidor antigo não manda: fica falso).</param>
/// <param name="StayConnected">No modo leve, domínios cuja aba nunca dorme (o do CRM).</param>
public sealed record TerminalConfig(int Version, string? AttendantName, bool ObservationMode, IReadOnlyList<ConfigItem> Items,
    IReadOnlyList<string>? ChromeAllowed = null, IReadOnlyList<string>? ChromeBlocked = null,
    IReadOnlyList<string>? AppsAllowed = null, IReadOnlyList<string>? AppsBlocked = null, bool StoreApps = false,
    bool LightMode = false, IReadOnlyList<string>? StayConnected = null)
{
    public ConfigItem? Find(string id) => Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
}

public sealed record MaintenanceInfo(string? Reason, string? Until);

/// <summary>Resposta de <c>POST /device/heartbeat</c>.</summary>
/// <param name="Assistant">A assistente de IA está ligada para esta máquina (servidor antigo não manda: fica falso).</param>
public sealed record HeartbeatResponse(string State, int ConfigVersion, string? AttendantName, MaintenanceInfo? Maintenance, bool ObservationMode, bool Assistant = false, bool WhatsAppLearning = false, string? Coverage = null, bool CoverageNow = false)
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

/// <summary>O TI está com a visualização ao vivo desta tela aberta? <c>Viewer</c> é o nome de quem está vendo.
/// <c>Controlling</c>: o TI também está mexendo no mouse e no teclado desta máquina agora.</summary>
public sealed record ScreenRequest(bool Wanted, string? Viewer, bool Controlling = false);

/// <summary>
/// Um comando do mouse ou do teclado vindo do TI (mesmos tipos de <c>Pontual.Server.Api.InputEvent</c>).
/// <c>move</c> usa <c>X</c>/<c>Y</c> de 0 a 1 na área de todos os monitores.
/// </summary>
public sealed record InputCommand(string Type, double X = 0, double Y = 0, int Button = 0, int Delta = 0, int Key = 0, string? Text = null);

/// <summary>Os comandos que o Terminal buscou de uma vez. <c>Controlling</c> falso = o controle acabou.</summary>
public sealed record ControlBatch(bool Controlling, IReadOnlyList<InputCommand> Events);

/// <summary>Como gravar a tela (decidido na Central). <c>Record</c> falso = não grava.</summary>
/// <remarks>StartMinute/EndMinute: janela do dia (hora local, em minutos desde 0h) em que se grava; fora dela, nada. Ausentes (Central antiga) = o dia todo.</remarks>
/// <remarks>FrameSeconds: de quantos em quantos segundos sai um quadro; acima de 1 (modo leve) vale no lugar de Fps.</remarks>
public sealed record RecordingPolicy(bool Record, int Fps, int MaxWidth, int Quality, int SegmentSeconds, int StartMinute = 0, int EndMinute = 1440, int FrameSeconds = 1)
{
    /// <summary>Segundos entre um quadro e outro.</summary>
    public double FrameInterval => FrameSeconds > 1 ? Math.Min(FrameSeconds, 5) : 1.0 / Math.Clamp(Fps, 1, 5);

    public bool InWindow(DateTime local)
    {
        var minute = local.Hour * 60 + local.Minute;
        return EndMinute <= StartMinute || (minute >= StartMinute && minute < EndMinute);
    }

    /// <summary>Segundos até a janela fechar (infinito se grava o dia todo).</summary>
    public double SecondsLeft(DateTime local) =>
        EndMinute <= StartMinute || EndMinute >= 1440 ? double.PositiveInfinity : (local.Date.AddMinutes(EndMinute) - local).TotalSeconds;
}

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
    /// <summary>
    /// A atendente abriu, pela lista "Aplicativos" do Terminal, um aplicativo do computador que o TI ainda não decidiu
    /// (item = nome do aplicativo, detail = caminho do .exe). Na primeira vez vira um pedido de liberação na Central.
    /// </summary>
    public const string AppOpened = "app_opened";
    /// <summary>Um aplicativo da lista "Aplicativos" não abriu, ou não está instalado na conta (item = nome, detail = motivo).</summary>
    public const string AppFailed = "app_failed";
    /// <summary>Resultado do Explorer escondido para os aplicativos da Microsoft Store (item = "Microsoft Store", detail = o que aconteceu).</summary>
    public const string StoreShell = "store_shell";
}
