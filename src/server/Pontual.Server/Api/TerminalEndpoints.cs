using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Audit;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Rotas do terminal (contrato, seção 5). Tudo o que chega aqui vem de uma máquina de atendente e é
/// tratado como não confiável: textos são limpos e cortados, datas fora de faixa são trocadas pela do
/// servidor, lotes têm limite de tamanho e o que não se reconhece é ignorado.
/// </summary>
public static class TerminalEndpoints
{
    public const string EnrollRateLimit = "enroll";
    public const string DeviceRateLimit = "device";

    private const int MaxEventsPerBatch = 100;
    private const int MaxAppsInUse = 50;

    private sealed record EnrollBody(string? Code, string? Hostname, string? Windows);
    private sealed record HeartbeatBody(List<string?>? AppsInUse, string? SessionStartedAt, string? Version = null, string? CoverageStatus = null);
    private sealed record EventBody(string? Type, string? Item, string? Detail, string? At);
    private sealed record EventsBody(List<EventBody?>? Events);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/device/enroll", EnrollAsync).RequireRateLimiting(EnrollRateLimit);
        api.MapPost("/device/heartbeat", HeartbeatAsync).Terminal().RequireRateLimiting(DeviceRateLimit);
        api.MapGet("/device/config", ConfigAsync).Terminal().RequireRateLimiting(DeviceRateLimit);
        api.MapPost("/device/events", EventsAsync).Terminal().RequireRateLimiting(DeviceRateLimit);
    }

    private static async Task<IResult> EnrollAsync(HttpContext context, PontualDb db, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var body = await context.ReadBodyAsync<EnrollBody>();
        var hostname = Input.Truncated(body.Hostname, 63);
        if (!Input.IsHostname(hostname))
        {
            throw ApiError.BadRequest("Nome da máquina inválido.");
        }

        var windows = Input.Truncated(body.Windows, 100);
        var now = time.GetUtcNow().UtcDateTime;

        const string invalid = "Código de cadastro inválido ou vencido.";
        var code = Secrets.NormalizeEnrollmentCode(body.Code) ?? throw ApiError.BadRequest(invalid);
        var hash = Secrets.Sha256(code);

        // Marcar como usado numa única instrução garante o uso único mesmo com duas tentativas simultâneas.
        var claimed = await db.EnrollmentCodes
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        if (claimed != 1)
        {
            throw ApiError.BadRequest(invalid);
        }

        var token = Secrets.NewToken();
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Hostname = hostname.ToUpperInvariant(),
            Windows = windows.Length == 0 ? null : windows,
            TokenHash = Secrets.Sha256(token),
            CreatedAt = now,
        };
        db.Devices.Add(device);
        db.Notifications.Add(new Notification
        {
            At = now,
            Text = $"A máquina {device.Hostname} foi cadastrada e aguarda aprovação",
        });

        await audit.SaveAsync(db, new AuditInfo(ActorType.Device, device.Id, device.Hostname, "device.enroll",
            $"A máquina {device.Hostname} foi cadastrada com um código e aguarda aprovação", "device", device.Id), cancellationToken: ct);

        return Results.Ok(new { deviceId = device.Id, token });
    }

    private static async Task<IResult> HeartbeatAsync(HttpContext context, PontualDb db, IConfiguration configuration, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        var body = await context.ReadBodyAsync<HeartbeatBody>();
        var now = time.GetUtcNow().UtcDateTime;

        device.LastSeenAt = now;
        device.AppsInUse = [.. (body.AppsInUse ?? [])
            .Take(MaxAppsInUse)
            .Select(name => Input.Truncated(name, 60))
            .Where(name => name.Length > 0)
            .Distinct()];
        if (Input.Truncated(body.Version, 20) is { Length: > 0 } terminalVersion)
        {
            device.TerminalVersion = terminalVersion;
        }

        if (body.CoverageStatus is not null)
        {
            device.CoverageStatus = Input.Truncated(body.CoverageStatus, 200);
            device.CoverageStatusAt = now;
        }

        device.SessionStartedAt = ParseTime(body.SessionStartedAt, now, maxAge: TimeSpan.FromDays(30), fallback: null);
        await db.SaveChangesAsync(ct);

        var version = await ConfigVersionAsync(db, ct);
        var state = !device.Approved ? "pending" : device.InMaintenance(now) ? "maintenance" : "active";
        var assistant = state == "active" && await AssistantEndpoints.IsEnabledAsync(db, configuration, device, ct);
        var coverage = assistant ? await CoverageEndpoints.ActiveModeAsync(db, configuration, device, now, ct) : CoverageMode.Off;
        return Results.Ok(new HeartbeatDto(state, version, device.AttendantName, MaintenanceDto.From(device, now), device.ObservationMode, assistant,
            assistant && await AssistantEndpoints.WhatsAppLearningAsync(db, configuration, device, ct),
            coverage,
            // A cobertura só roda no dia da falta, então o Terminal responde na hora, sem esperar o computador ficar parado.
            coverage != CoverageMode.Off));
    }

    private static async Task<IResult> ConfigAsync(HttpContext context, PontualDb db, CancellationToken ct)
    {
        var device = context.Device();
        var version = await ConfigVersionAsync(db, ct);

        // Máquina ainda não aprovada não recebe nenhum item.
        var items = device.Approved ? await VisibleItemsAsync(db, device, ct) : [];
        var (chromeAllowed, chromeBlocked) = device.Approved ? await ChromeEndpoints.DecidedAsync(db, device.Id, ct) : ([], []);
        var (appsAllowed, appsBlocked) = device.Approved ? await AppEndpoints.DecidedAsync(db, device.Id, ct) : ([], []);

        return Results.Ok(new ConfigDto(
            version,
            device.AttendantName,
            device.ObservationMode,
            [.. items.Select(i => new ConfigItemDto(i.Id, i.Name, i.Kind, i.Target, i.Group, i.Hosts()))],
            chromeAllowed,
            chromeBlocked,
            appsAllowed,
            appsBlocked,
            device.Approved && device.StoreApps,
            device.Approved && device.LightMode,
            device.Approved && device.LightMode ? await StayConnectedAsync(db, ct) : null));
    }

    /// <summary>O domínio do CRM, pelo endereço que o TI cadastrou: a aba dele recebe mensagens e não pode dormir.</summary>
    private static async Task<IReadOnlyList<string>> StayConnectedAsync(PontualDb db, CancellationToken ct)
    {
        var url = await db.TextSettings.AsNoTracking().Where(s => s.Key == TextSetting.CrmUrl).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? [uri.Host] : [];
    }

    private static async Task<IResult> EventsAsync(HttpContext context, PontualDb db, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        var body = await context.ReadBodyAsync<EventsBody>();
        var events = body.Events ?? [];
        if (events.Count > MaxEventsPerBatch)
        {
            throw ApiError.BadRequest($"Envie no máximo {MaxEventsPerBatch} eventos por vez.");
        }

        // Máquina não aprovada não gera histórico, alerta nem notificação.
        if (!device.Approved || events.Count == 0)
        {
            return Results.NoContent();
        }

        var now = time.GetUtcNow().UtcDateTime;
        var items = await VisibleItemsAsync(db, device, ct);
        var allowedHosts = items.SelectMany(i => i.Hosts()).Distinct().ToList();
        var openAlerts = await db.Alerts.Where(a => a.DeviceId == device.Id && a.ResolvedAt == null).ToListAsync(ct);
        var label = device.Label;
        (List<string> Allowed, List<string> Blocked)? chrome = null;
        (List<string> Allowed, List<string> Blocked)? apps = null;

        foreach (var e in events)
        {
            if (e is null)
            {
                continue;
            }

            var at = ParseTime(e.At, now, maxAge: TimeSpan.FromDays(7), fallback: now)!.Value;
            var item = Input.Truncated(e.Item, 60);

            switch (e.Type)
            {
                case "session_started":
                    AddHistory(db, device, at, "Abriu o terminal");
                    break;

                case "opened" when item.Length > 0:
                    AddHistory(db, device, at, $"Abriu {item}");
                    AddNotification(db, now, $"{label} abriu {item}", urgent: false);
                    break;

                case "closed" when item.Length > 0:
                    AddHistory(db, device, at, $"Fechou {item}");
                    break;

                // Explorer escondido para os aplicativos da Microsoft Store: se o Windows aceitou ou não (detail).
                case "store_shell" when Input.Truncated(e.Detail, 300) is { Length: > 0 } storeShell:
                    AddHistory(db, device, at, $"Aplicativos da Microsoft Store: {storeShell}");
                    break;

                // Aplicativo da lista "Aplicativos" que não abriu ou não está instalado na conta (detail = motivo do Windows).
                case "app_failed" when item.Length > 0:
                    var reason = Input.Truncated(e.Detail, 300);
                    AddHistory(db, device, at, reason.Length > 0 ? $"Não conseguiu abrir {item}: {reason}" : $"Não conseguiu abrir {item}");
                    if (RaiseAlert(db, device, openAlerts, now, $"Não conseguiu abrir {item}"))
                    {
                        AddNotification(db, now, $"{label} não conseguiu abrir {item}", urgent: false);
                    }

                    break;

                case "blocked" when Input.TryHost(e.Detail, out var host):
                    AddHistory(db, device, at, $"Tentou acessar {host}, que não está liberado");
                    // Um alerta aberto igual não se repete: a mesma tentativa em sequência não inunda o painel.
                    if (RaiseAlert(db, device, openAlerts, now, $"Tentou acessar {host}, que não está liberado"))
                    {
                        AddNotification(db, now, $"{label} tentou acessar {host}, que não está liberado", urgent: true);
                    }

                    break;

                // Celular: a atendente saiu do Terminal para um aplicativo que não está liberado (detail = nome do app).
                case "app_blocked" when Input.Truncated(e.Detail, 80) is { Length: > 0 } appName:
                    AddHistory(db, device, at, $"Abriu o aplicativo {appName}, que não está liberado");
                    if (RaiseAlert(db, device, openAlerts, now, $"Abriu o aplicativo {appName}, que não está liberado"))
                    {
                        AddNotification(db, now, $"{label} abriu o aplicativo {appName}, que não está liberado", urgent: true);
                    }

                    break;

                case "observed" when Input.TryHost(e.Detail, out var host):
                    // Domínio que já está liberado para a máquina não é novidade (o terminal pode estar com a lista antiga).
                    if (!Input.HostMatches(host, allowedHosts))
                    {
                        await ObserveAsync(db, device, items, host, item, at, now, ct);
                    }

                    break;

                // Navegadores (Chrome, Edge, Opera, Firefox): domínio na aba visível que não é operacional para a máquina.
                // "chrome_stay" chega a cada minuto em que ele continua na tela. O item é o nome do navegador (Terminal
                // antigo manda sempre "Google Chrome"). A atendente não vê nada; o TI decide na Central.
                case "chrome_visit" or "chrome_stay" when Input.TryHost(e.Detail, out var host):
                    chrome ??= await ChromeEndpoints.DecidedAsync(db, device.Id, ct);
                    await ChromeAsync(db, device, allowedHosts, chrome.Value, openAlerts, host, item.Length > 0 ? item : "navegador",
                        e.Type == "chrome_stay", at, now, ct);
                    break;

                // Computador: a atendente abriu, pela lista "Aplicativos" do Terminal, um aplicativo que o TI ainda não
                // decidiu (detail = caminho do .exe). Vira um pedido dela; o TI libera ou bloqueia na Central.
                case "app_opened" when Input.TryExe(e.Detail, out var exe):
                    apps ??= await AppEndpoints.DecidedAsync(db, device.Id, ct);
                    await AppAsync(db, device, items, apps.Value, openAlerts, exe, item.Length > 0 ? item : exe, Input.Truncated(e.Detail, 260), at, now, ct);
                    break;

                case "help":
                    if (RaiseAlert(db, device, openAlerts, now, "Chamou o TI"))
                    {
                        AddNotification(db, now, $"{label} chamou o TI", urgent: false);
                    }

                    break;

                // Tipo desconhecido ou evento sem o dado obrigatório: ignorado.
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task ObserveAsync(PontualDb db, Device device, List<Item> items, string host, string itemName, DateTime at, DateTime now, CancellationToken ct)
    {
        var known = items.FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
        var observed = db.ObservedHosts.Local.FirstOrDefault(o => o.Host == host)
            ?? await db.ObservedHosts.FirstOrDefaultAsync(o => o.Host == host, ct);

        if (observed is null)
        {
            observed = new ObservedHost { Id = Guid.NewGuid(), Host = host, LastAt = at };
            db.ObservedHosts.Add(observed);
            AddNotification(db, now,
                itemName.Length > 0
                    ? $"{device.Label} acessou o domínio novo {host} a partir de {itemName} (modo de observação)"
                    : $"{device.Label} acessou o domínio novo {host} (modo de observação)",
                urgent: false);
        }

        observed.Count = Math.Min(observed.Count + 1, int.MaxValue - 1);
        observed.DeviceId = device.Id;
        observed.ItemId = known?.Id ?? observed.ItemId;
        observed.ItemName = known?.Name ?? (itemName.Length > 0 ? itemName : observed.ItemName);
        if (at > observed.LastAt)
        {
            observed.LastAt = at;
        }
    }

    private static async Task ChromeAsync(PontualDb db, Device device, List<string> allowedHosts, (List<string> Allowed, List<string> Blocked) decided,
        List<Alert> openAlerts, string host, string browser, bool stay, DateTime at, DateTime now, CancellationToken ct)
    {
        // Operacional para a máquina, ou já liberado pelo TI: o Terminal pode estar com a lista antiga.
        if (!host.Contains('.') || Input.HostMatches(host, allowedHosts) || Input.HostMatches(host, decided.Allowed))
        {
            return;
        }

        var blocked = Input.HostMatches(host, decided.Blocked);
        if (blocked)
        {
            if (stay)
            {
                return;
            }

            AddHistory(db, device, at, $"Tentou abrir {host} no {browser}, que o TI bloqueou");

            // Bloqueado só para ela: o TI já decidiu para ela, não pede de novo. Bloqueado para todas: continua
            // bloqueado, mas vira pedido dela, para o TI decidir se ela pode usar (pedido do dono, 20/11/2026).
            var mineBlocked = await db.ChromeDeviceSites.AsNoTracking()
                .Where(c => c.DeviceId == device.Id && c.Status == ChromeSite.StatusBlocked)
                .Select(c => c.Host)
                .ToListAsync(ct);
            if (Input.HostMatches(host, mineBlocked))
            {
                return;
            }
        }

        // Cada atendente tem o próprio pedido: o TI decide só para ela ou para todas.
        var site = db.ChromeDeviceSites.Local.FirstOrDefault(c => c.DeviceId == device.Id && c.Host == host)
            ?? await db.ChromeDeviceSites.FirstOrDefaultAsync(c => c.DeviceId == device.Id && c.Host == host, ct);
        if (site is null)
        {
            // Um "continua aberto" sem a visita (lote perdido) conta como a visita.
            stay = false;
            site = new ChromeDeviceSite { Id = Guid.NewGuid(), DeviceId = device.Id, Host = host, FirstAt = at, LastAt = at };
            db.ChromeDeviceSites.Add(site);
        }

        site.Browser = browser == "navegador" ? site.Browser : browser;
        if (at > site.LastAt)
        {
            site.LastAt = at;
        }

        if (stay)
        {
            site.Minutes = Math.Min(site.Minutes + 1, 10_000_000);
            return;
        }

        site.Visits = Math.Min(site.Visits + 1, int.MaxValue - 1);
        if (!blocked)
        {
            AddHistory(db, device, at, $"Abriu {host} no {browser}, que não é um site operacional");
        }

        if (RaiseAlert(db, device, openAlerts, now, ChromeEndpoints.AlertText(host)))
        {
            AddNotification(db, now, blocked
                ? $"{device.Label} tentou abrir {host} no {browser}, que está bloqueado para todas. Decida se ela pode usar."
                : $"{device.Label} abriu {host} no {browser}, que não é um site operacional", urgent: true);
        }
    }

    private static async Task AppAsync(PontualDb db, Device device, List<Item> items, (List<string> Allowed, List<string> Blocked) decided,
        List<Alert> openAlerts, string exe, string name, string path, DateTime at, DateTime now, CancellationToken ct)
    {
        // Programa cadastrado para a máquina, ou já liberado pelo TI: o Terminal pode estar com a lista antiga.
        if (decided.Allowed.Contains(exe) || items.Any(i => i.Kind == ItemKind.Program && string.Equals(i.Target, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var blocked = decided.Blocked.Contains(exe);
        if (blocked)
        {
            AddHistory(db, device, at, $"Tentou abrir o aplicativo {name} ({exe}), que o TI bloqueou");

            // Bloqueado só para ela: o TI já decidiu para ela, não pede de novo. Bloqueado para todas: continua
            // bloqueado, mas vira pedido dela, para o TI decidir se ela pode usar (mesma regra dos sites nos navegadores).
            var mineBlocked = await db.DeviceApps.AsNoTracking()
                .AnyAsync(a => a.DeviceId == device.Id && a.Exe == exe && a.Status == AppDecision.StatusBlocked, ct);
            if (mineBlocked)
            {
                return;
            }
        }

        var app = db.DeviceApps.Local.FirstOrDefault(a => a.DeviceId == device.Id && a.Exe == exe)
            ?? await db.DeviceApps.FirstOrDefaultAsync(a => a.DeviceId == device.Id && a.Exe == exe, ct);
        if (app is null)
        {
            app = new DeviceApp { Id = Guid.NewGuid(), DeviceId = device.Id, Exe = exe, FirstAt = at, LastAt = at };
            db.DeviceApps.Add(app);
        }

        app.Name = name;
        app.Path = path;
        app.Opens = Math.Min(app.Opens + 1, int.MaxValue - 1);
        if (at > app.LastAt)
        {
            app.LastAt = at;
        }

        if (!blocked)
        {
            AddHistory(db, device, at, $"Abriu o aplicativo {name} ({exe}), que ainda não foi liberado");
        }

        if (RaiseAlert(db, device, openAlerts, now, AppEndpoints.AlertText(exe)))
        {
            AddNotification(db, now, blocked
                ? $"{device.Label} tentou abrir o aplicativo {name} ({exe}), que está bloqueado para todas. Decida se ela pode usar."
                : $"{device.Label} abriu o aplicativo {name} ({exe}) pela primeira vez. Decida se ela pode usar.", urgent: true);
        }
    }

    private static async Task<List<Item>> VisibleItemsAsync(PontualDb db, Device device, CancellationToken ct)
    {
        var items = await ItemEndpoints.Ordered(db.Items.AsNoTracking().Where(i => i.Enabled && !i.Pending)).ToListAsync(ct);
        // No celular não aparece nenhum banco: o grupo "Bancos e promotoras" fica só nos computadores.
        var phone = SharedPhoneEndpoints.IsPhone(device);
        return [.. items.Where(i => i.VisibleTo(device.Id) && !(phone && i.Group == Input.Groups[0]))];
    }

    private static async Task<long> ConfigVersionAsync(PontualDb db, CancellationToken ct) =>
        await db.Settings.AsNoTracking().Where(s => s.Key == Setting.ConfigVersion).Select(s => s.Value).FirstAsync(ct);

    private static void AddHistory(PontualDb db, Device device, DateTime at, string text) =>
        db.History.Add(new HistoryEntry { DeviceId = device.Id, At = at, Text = text });

    private static void AddNotification(PontualDb db, DateTime now, string text, bool urgent) =>
        db.Notifications.Add(new Notification { At = now, Text = text, Urgent = urgent });

    /// <summary>Cria o alerta, a menos que já exista um aberto igual para a máquina. Devolve se criou.</summary>
    private static bool RaiseAlert(PontualDb db, Device device, List<Alert> openAlerts, DateTime now, string text)
    {
        if (openAlerts.FirstOrDefault(a => a.Text == text) is { } existing)
        {
            existing.At = now;
            return false;
        }

        var alert = new Alert { Id = Guid.NewGuid(), DeviceId = device.Id, Text = text, At = now };
        db.Alerts.Add(alert);
        openAlerts.Add(alert);
        return true;
    }

    /// <summary>Data vinda do terminal: só vale se estiver entre "agora - maxAge" e "agora + 5 minutos".</summary>
    private static DateTime? ParseTime(string? raw, DateTime now, TimeSpan maxAge, DateTime? fallback)
    {
        if (raw is { Length: <= 40 }
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            var utc = parsed.UtcDateTime;
            if (utc >= now - maxAge && utc <= now.AddMinutes(5))
            {
                return utc > now ? now : utc;
            }
        }

        return fallback;
    }
}
