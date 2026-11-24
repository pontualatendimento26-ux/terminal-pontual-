using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Data;

namespace Pontual.Server.Api;

/// <summary>
/// Um comando do mouse ou do teclado que o administrador mandou para a máquina da atendente.
/// <c>Type</c>: <c>move</c> (ponteiro, com <c>X</c>/<c>Y</c> de 0 a 1 na área de todos os monitores),
/// <c>down</c>/<c>up</c> (botão 0 esquerdo, 1 meio, 2 direito), <c>wheel</c> (<c>Delta</c>),
/// <c>key</c>/<c>keyup</c> (<c>Key</c> = código de tecla do Windows) e <c>text</c> (<c>Text</c> digitado).
/// Nada disto é gravado: a fila vive na memória e morre com o controle.
/// </summary>
public sealed record InputEvent(string Type, double X = 0, double Y = 0, int Button = 0, int Delta = 0, int Key = 0, string? Text = null)
{
    private static readonly string[] Allowed = ["move", "down", "up", "wheel", "key", "keyup", "text"];

    /// <summary>O mesmo comando dentro dos limites aceitos, ou nulo se não for um comando conhecido.</summary>
    public InputEvent? Sanitized()
    {
        if (Array.IndexOf(Allowed, Type) < 0)
        {
            return null;
        }

        // Texto longo demais seria digitação em massa: corta no tamanho de uma frase curta.
        if (Type == "text" && string.IsNullOrEmpty(Text))
        {
            return null;
        }

        if ((Type == "key" || Type == "keyup") && Key is < 1 or > 254)
        {
            return null;
        }

        return new InputEvent(
            Type,
            Math.Clamp(X, 0, 1),
            Math.Clamp(Y, 0, 1),
            Math.Clamp(Button, 0, 2),
            Math.Clamp(Delta, -3000, 3000),
            Key,
            Text is { Length: > 64 } ? Text[..64] : Text);
    }
}

/// <summary>
/// Visualização ao vivo da tela de uma máquina e, em cima dela, o controle do mouse e do teclado. Regras da casa:
/// só existe enquanto um administrador está com a janela aberta (o pedido vence sozinho em segundos);
/// só a ÚLTIMA imagem fica guardada, e só na memória (nada vai para o banco nem para o disco);
/// todo início de visualização e de controle entra na auditoria; e o terminal mostra um aviso à atendente
/// enquanto envia a tela ou enquanto está sendo controlado.
/// </summary>
public sealed class ScreenWatch(TimeProvider time)
{
    /// <summary>Quanto tempo o pedido vale sem ser renovado. O painel renova a cada imagem que busca.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(15);

    /// <summary>O controle vence mais rápido que a visualização: se o painel calar, o mouse volta a ser só da atendente.</summary>
    public static readonly TimeSpan ControlLease = TimeSpan.FromSeconds(10);

    /// <summary>Teto da fila de comandos. Se o terminal parar de buscar, a fila não cresce sem fim.</summary>
    private const int MaxQueued = 500;

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public void Start(Guid deviceId, string viewer) => _entries[deviceId] = new Entry { Until = Now + Lease, Viewer = viewer };

    public bool Renew(Guid deviceId)
    {
        if (Active(deviceId) is not { } entry)
        {
            return false;
        }

        entry.Until = Now + Lease;
        return true;
    }

    public void Stop(Guid deviceId) => _entries.TryRemove(deviceId, out _);

    /// <summary>Nome de quem está vendo, ou nulo se ninguém está.</summary>
    public string? Viewer(Guid deviceId) => Active(deviceId)?.Viewer;

    public bool Put(Guid deviceId, byte[] frame)
    {
        if (Active(deviceId) is not { } entry)
        {
            return false;
        }

        entry.Frame = frame;
        return true;
    }

    public byte[]? Latest(Guid deviceId) => Active(deviceId)?.Frame;

    /// <summary>Assume o controle. Falso se ninguém está vendo a tela: não se controla o que não se vê.</summary>
    public bool StartControl(Guid deviceId, string viewer)
    {
        if (Active(deviceId) is not { } entry)
        {
            return false;
        }

        entry.Viewer = viewer;
        entry.ControlUntil = Now + ControlLease;
        return true;
    }

    /// <summary>Solta o controle na hora e joga fora o que ainda não foi aplicado.</summary>
    public void StopControl(Guid deviceId)
    {
        if (_entries.TryGetValue(deviceId, out var entry))
        {
            entry.ControlUntil = DateTime.MinValue;
            entry.Input.Clear();
        }
    }

    /// <summary>Alguém está com o controle desta máquina agora?</summary>
    public bool Controlling(Guid deviceId) => Active(deviceId) is { } entry && entry.ControlUntil >= Now;

    /// <summary>Guarda os comandos para o terminal buscar. Falso se o controle não está ativo.</summary>
    public bool Send(Guid deviceId, IEnumerable<InputEvent> events)
    {
        if (Active(deviceId) is not { } entry || entry.ControlUntil < Now)
        {
            return false;
        }

        // Mandar comando (ou só o sinal de vida, com a lista vazia) renova o controle e a visualização.
        entry.ControlUntil = Now + ControlLease;
        entry.Until = Now + Lease;
        foreach (var command in events)
        {
            if (command.Sanitized() is { } clean && entry.Input.Count < MaxQueued)
            {
                entry.Input.Enqueue(clean);
            }
        }

        return true;
    }

    /// <summary>Tira da fila tudo o que chegou até agora, na ordem em que o administrador fez.</summary>
    public List<InputEvent> Take(Guid deviceId)
    {
        var commands = new List<InputEvent>();
        if (_entries.TryGetValue(deviceId, out var entry))
        {
            while (entry.Input.TryDequeue(out var command))
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    private Entry? Active(Guid deviceId)
    {
        if (!_entries.TryGetValue(deviceId, out var entry))
        {
            return null;
        }

        if (entry.Until < Now)
        {
            // Venceu: a imagem guardada e a fila de comandos são descartadas junto.
            _entries.TryRemove(deviceId, out _);
            return null;
        }

        return entry;
    }

    private sealed class Entry
    {
        public DateTime Until;
        public string Viewer = "";
        public byte[]? Frame;

        /// <summary>Até quando o controle vale. <see cref="DateTime.MinValue"/> = ninguém controlando.</summary>
        public DateTime ControlUntil = DateTime.MinValue;

        public ConcurrentQueue<InputEvent> Input = new();
    }
}

public static class ScreenEndpoints
{
    /// <summary>
    /// Teto próprio para a tela e o controle: as imagens rápidas e a busca de comandos passam
    /// do teto geral de 300 chamadas por minuto da máquina, e só correm com um administrador olhando.
    /// </summary>
    public const string ScreenRateLimit = "screen-device";

    private const int MaxFrameBytes = 2 * 1024 * 1024;

    /// <summary>Quanto tempo a busca de comandos do terminal fica esperando antes de responder vazia.</summary>
    private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(8);

    public static void Map(RouteGroupBuilder api)
    {
        // Painel: ver a tela de uma pessoa é leitura de uso individual.
        api.MapPost("/devices/{id:guid}/screen", StartAsync).Panel(Permission.UsageReadIndividual);
        api.MapGet("/devices/{id:guid}/screen", Frame).Panel(Permission.UsageReadIndividual);
        api.MapDelete("/devices/{id:guid}/screen", Stop).Panel(Permission.UsageReadIndividual);

        // Painel: mexer no mouse e no teclado da atendente é permissão própria, só de administrador.
        api.MapPost("/devices/{id:guid}/control", StartControlAsync).Panel(Permission.ScreenControl);
        api.MapPost("/devices/{id:guid}/control/input", Input).Panel(Permission.ScreenControl);
        api.MapDelete("/devices/{id:guid}/control", StopControlAsync).Panel(Permission.ScreenControl);

        // Terminal: pergunta se alguém quer ver e, enquanto quiserem, manda uma imagem por vez.
        api.MapGet("/device/screen", Wanted).Terminal().RequireRateLimiting(ScreenRateLimit);
        api.MapPost("/device/screen", UploadAsync).Terminal().RequireRateLimiting(ScreenRateLimit);

        // Terminal: busca os comandos do mouse e do teclado para aplicar no Windows.
        api.MapGet("/device/control", ControlAsync).Terminal().RequireRateLimiting(ScreenRateLimit);
    }

    private static async Task<IResult> StartAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, ScreenWatch watch, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        if (!device.Approved)
        {
            throw ApiError.Conflict("A máquina ainda não foi aprovada.");
        }

        await audit.SaveAsync(db, AuditInfo.By(admin, "screen.view",
            $"{admin.Name} abriu a visualização ao vivo da tela de {device.Label}", "device", device.Id), cancellationToken: ct);

        watch.Start(device.Id, admin.Name);
        return Results.NoContent();
    }

    private static IResult Frame(Guid id, HttpContext context, ScreenWatch watch)
    {
        // Buscar a imagem renova o pedido. Se ele já venceu, o painel precisa pedir de novo (e isso é auditado de novo).
        if (!watch.Renew(id))
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        context.Response.Headers.CacheControl = "no-store";
        return watch.Latest(id) is { } frame ? Results.File(frame, "image/jpeg") : Results.NoContent();
    }

    private static IResult Stop(Guid id, ScreenWatch watch)
    {
        watch.Stop(id);
        return Results.NoContent();
    }

    /// <summary>
    /// O administrador assume o mouse e o teclado da máquina. Exige a visualização da tela aberta
    /// (não se controla o que não se vê) e fica na auditoria, com nome, máquina e hora.
    /// O que for digitado NÃO é registrado.
    /// </summary>
    private static async Task<IResult> StartControlAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, ScreenWatch watch, CancellationToken ct)
    {
        var admin = context.Admin();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiError.NotFound("Máquina não encontrada.");
        if (!device.Approved)
        {
            throw ApiError.Conflict("A máquina ainda não foi aprovada.");
        }

        if (!watch.StartControl(device.Id, admin.Name))
        {
            throw ApiError.Conflict("Abra a visualização da tela antes de assumir o controle.");
        }

        await audit.SaveAsync(db, AuditInfo.By(admin, "screen.control",
            $"{admin.Name} assumiu o controle do mouse e do teclado de {device.Label}", "device", device.Id), cancellationToken: ct);

        return Results.NoContent();
    }

    private static IResult Input(Guid id, ScreenWatch watch, List<InputEvent> events)
    {
        if (events.Count > 200)
        {
            throw ApiError.BadRequest("Comandos demais de uma vez.");
        }

        // Falso = o controle venceu ou foi solto: o painel pede de novo (e a auditoria registra de novo).
        return watch.Send(id, events) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status410Gone);
    }

    private static async Task<IResult> StopControlAsync(Guid id, HttpContext context, PontualDb db, AuditWriter audit, ScreenWatch watch, CancellationToken ct)
    {
        var admin = context.Admin();
        var controlava = watch.Controlling(id);
        watch.StopControl(id);
        if (controlava && await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) is { } device)
        {
            await audit.SaveAsync(db, AuditInfo.By(admin, "screen.control.end",
                $"{admin.Name} soltou o controle de {device.Label}", "device", device.Id), cancellationToken: ct);
        }

        return Results.NoContent();
    }

    private static IResult Wanted(HttpContext context, ScreenWatch watch)
    {
        var device = context.Device();
        var viewer = device.Approved ? watch.Viewer(device.Id) : null;
        var controlling = viewer is not null && watch.Controlling(device.Id);
        return Results.Ok(new { wanted = viewer is not null, viewer, controlling });
    }

    /// <summary>
    /// O terminal busca aqui os comandos do mouse e do teclado. A chamada espera alguns segundos pelo
    /// próximo comando em vez de responder vazia na hora: assim o clique do TI chega rápido sem o
    /// terminal perguntar sem parar. <c>controlling</c> falso = acabou, e o terminal tira o aviso da tela.
    /// </summary>
    private static async Task<IResult> ControlAsync(HttpContext context, ScreenWatch watch, TimeProvider time, CancellationToken ct)
    {
        var device = context.Device();
        if (!device.Approved)
        {
            return Results.Ok(new { controlling = false, events = Array.Empty<InputEvent>() });
        }

        var deadline = time.GetUtcNow() + PollWait;
        while (!ct.IsCancellationRequested)
        {
            var controlling = watch.Controlling(device.Id);
            var commands = watch.Take(device.Id);
            if (!controlling || commands.Count > 0 || time.GetUtcNow() >= deadline)
            {
                return Results.Ok(new { controlling, events = commands });
            }

            await Task.Delay(TimeSpan.FromMilliseconds(40), time, ct);
        }

        return Results.Ok(new { controlling = false, events = Array.Empty<InputEvent>() });
    }

    private static async Task<IResult> UploadAsync(HttpContext context, ScreenWatch watch, CancellationToken ct)
    {
        var device = context.Device();
        if (!device.Approved || watch.Viewer(device.Id) is null)
        {
            // Ninguém pediu: a imagem nem é lida.
            return Results.Ok(new { wanted = false, controlling = false });
        }

        // Uma tela inteira não cabe no limite geral de 256 KB; só esta rota aceita mais.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxFrameBytes;
        }

        if (context.Request.ContentLength is > MaxFrameBytes)
        {
            throw ApiError.BadRequest("Imagem grande demais.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxFrameBytes)
            {
                throw ApiError.BadRequest("Imagem grande demais.");
            }

            buffer.Write(chunk, 0, read);
        }

        var frame = buffer.ToArray();
        // Só JPEG: o painel mostra o que chega aqui, então nada além de imagem é aceito.
        if (frame.Length < 4 || frame[0] != 0xFF || frame[1] != 0xD8 || frame[2] != 0xFF)
        {
            throw ApiError.BadRequest("A imagem precisa ser JPEG.");
        }

        return Results.Ok(new { wanted = watch.Put(device.Id, frame), controlling = watch.Controlling(device.Id) });
    }
}
