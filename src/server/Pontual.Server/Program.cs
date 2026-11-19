using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Authorization;
using Pontual.Server.Api;
using Pontual.Server.Data;

// Versão publicada iniciada de outra pasta: as telas (wwwroot) estão ao lado do executável, não na pasta atual.
var besideExecutable = !Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"))
    && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

var builder = WebApplication.CreateBuilder(besideExecutable
    ? new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory }
    : new WebApplicationOptions { Args = args });

// Porta: padrão 5080 (appsettings.json, Kestrel:Endpoints:Http:Url). PONTUAL_PORT troca só a porta.
if (int.TryParse(Environment.GetEnvironmentVariable("PONTUAL_PORT"), out var port) && port is > 0 and < 65536)
{
    builder.Configuration["Kestrel:Endpoints:Http:Url"] = $"http://0.0.0.0:{port}";
}

// Nenhuma rota precisa de corpo grande; o maior é um lote de 100 eventos.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);

builder.Services.AddDbContext<PontualDb>((services, options) =>
{
    // Lido aqui, e não antes, para valer também a configuração injetada pelos testes.
    var configured = services.GetRequiredService<IConfiguration>()["Database:Path"];
    var path = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(AppContext.BaseDirectory, "data", "pontual.db")
        : Path.GetFullPath(configured);
    options.UseSqlite($"Data Source={path}");
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuditWriter>();
builder.Services.AddSingleton<ScreenWatch>();
builder.Services.AddSingleton<Pontual.Server.Assistant.IAssistantModel, Pontual.Server.Assistant.ClaudeAssistantModel>();
// Lê as conversas novas das atendentes de tempos em tempos (perfil de atendimento e sugestões para a base).
builder.Services.AddHostedService<Pontual.Server.Assistant.AnalysisWorker>();
builder.Services.AddSingleton<IPasswordHasher<Admin>, PasswordHasher<Admin>>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ErrorBody("Muitas tentativas em pouco tempo. Aguarde um minuto e tente de novo."), cancellationToken);

    static string Client(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "local";

    // Senha e código de cadastro: poucas tentativas por minuto por endereço, contra adivinhação.
    options.AddPolicy(AccountEndpoints.LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        "login:" + Client(context), _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy(TerminalEndpoints.EnrollRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        "enroll:" + Client(context), _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));

    // Um terminal normal faz umas 10 chamadas por minuto; o teto contém um terminal defeituoso ou malicioso.
    options.AddPolicy(TerminalEndpoints.DeviceRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        "device:" + Secrets.Sha256(context.Request.Headers.Authorization.ToString()),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));

    // Cada pergunta à assistente custa uma chamada paga à IA: o teto por máquina é bem mais baixo.
    options.AddPolicy(AssistantEndpoints.RateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        "assistant:" + Secrets.Sha256(context.Request.Headers.Authorization.ToString()),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);

// Erros previstos (ApiError) e imprevistos saem sempre como { "error": "…" }.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception exception) when (!context.Response.HasStarted && exception is not OperationCanceledException)
    {
        var (status, message) = exception switch
        {
            ApiError api => (api.Status, api.Message),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => (StatusCodes.Status413PayloadTooLarge, "Os dados enviados são grandes demais."),
            BadHttpRequestException bad => (bad.StatusCode, "Requisição inválida."),
            _ => (StatusCodes.Status500InternalServerError, "Erro interno do servidor."),
        };

        if (status >= 500)
        {
            app.Logger.LogError(exception, "Erro não tratado em {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ErrorBody(message));
    }
});

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers.XFrameOptions = "SAMEORIGIN";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.CacheControl = "no-store";
    }

    await next(context);
});

// Telas: wwwroot/central e wwwroot/terminal, com index.html como padrão de cada pasta.
app.UseDefaultFiles();
var tiposDeArquivo = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
// O app do celular (.apk) é baixado pela página /baixar; sem este mapa o servidor recusaria o arquivo.
tiposDeArquivo.Mappings[".apk"] = "application/vnd.android.package-archive";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = tiposDeArquivo,
    // Sem build nem nomes com hash: o navegador deve sempre conferir se o arquivo mudou.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

app.UseRouting();

// Autenticação e autorização, antes do limitador e antes de qualquer leitura do corpo.
app.Use(async (context, next) =>
{
    var endpoint = context.GetEndpoint();

    if (endpoint?.Metadata.GetMetadata<PanelRoute>() is { } panel)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers["X-Pontual"] != "1")
        {
            throw ApiError.BadRequest("Requisição recusada: falta o cabeçalho X-Pontual.");
        }

        var db = context.RequestServices.GetRequiredService<PontualDb>();
        Admin? admin = null;
        if (context.Request.Cookies.TryGetValue(AccountEndpoints.CookieName, out var cookie) && cookie is { Length: > 0 and <= 128 })
        {
            var hash = Secrets.Sha256(cookie);
            var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == hash && s.ExpiresAt > now);
            if (session is not null)
            {
                admin = await db.Admins.FirstOrDefaultAsync(a => a.Id == session.AdminId && a.Active);
            }
        }

        if (admin is not null)
        {
            context.Items[HttpContextExtensions.AdminKey] = admin;
        }
        else if (panel.RequireAuth)
        {
            throw new ApiError(StatusCodes.Status401Unauthorized, "Entre no painel para continuar.");
        }

        if (panel.Permission is { } permission && !PermissionMatrix.IsAllowed(admin!.Role, permission))
        {
            throw ApiError.Forbidden();
        }
    }
    else if (endpoint?.Metadata.GetMetadata<TerminalRoute>() is not null)
    {
        const string scheme = "Bearer ";
        var header = context.Request.Headers.Authorization.ToString();
        Device? device = null;
        if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) && header.Length <= scheme.Length + 128)
        {
            var hash = Secrets.Sha256(header[scheme.Length..].Trim());
            device = await context.RequestServices.GetRequiredService<PontualDb>().Devices.FirstOrDefaultAsync(d => d.TokenHash == hash);
        }

        // Token desconhecido ou de máquina removida.
        context.Items[HttpContextExtensions.DeviceKey] = device
            ?? throw new ApiError(StatusCodes.Status401Unauthorized, "Máquina não reconhecida. Cadastre-a de novo.");
    }

    await next(context);
});

app.UseRateLimiter();

app.MapGet("/", () => Results.Redirect("/central/"));

var api = app.MapGroup("/api/v1");
api.MapGet("/health", () => Results.Ok(new { ok = true, version = "1.0.0" }));
AccountEndpoints.Map(api);
DeviceEndpoints.Map(api);
ItemEndpoints.Map(api);
MonitorEndpoints.Map(api);
TerminalEndpoints.Map(api);
ScreenEndpoints.Map(api);
AssistantEndpoints.Map(api);
NeuralEndpoints.Map(api);

app.Map("/api/{**rest}", () => Results.Json(new ErrorBody("Rota não encontrada."), statusCode: StatusCodes.Status404NotFound));

app.Run();

/// <summary>Exposto para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
