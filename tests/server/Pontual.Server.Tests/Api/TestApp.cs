using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Pontual.Server.Tests.Api;

/// <summary>Servidor de verdade em memória, com um banco SQLite em arquivo temporário só deste teste.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string OwnerEmail = "paulo@pontual.test";
    public const string Password = "senha-de-teste-123";

    /// <summary>Pasta das gravações de tela deste teste (nunca a pasta de verdade).</summary>
    public string RecordingsPath { get; } = Path.Combine(Path.GetTempPath(), $"pontual-gravacoes-{Guid.NewGuid():N}");

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pontual-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Path", _databasePath);
        builder.UseSetting("Recordings:Path", RecordingsPath);
        builder.UseSetting("Recordings:MinFreeGB", "0");
        if (AssistantModel is { } model)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(model));
        }

        if (Clock is { } clock)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(clock));
        }

        if (ExtraServices is { } extra)
        {
            builder.ConfigureTestServices(extra);
        }
    }

    /// <summary>IA de mentira para os testes da assistente; definir antes de criar o primeiro cliente.</summary>
    public Pontual.Server.Assistant.IAssistantModel? AssistantModel { get; init; }

    /// <summary>Troca de serviços só deste teste (ex.: o cliente do CRM falando com um CRM de mentira).</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    /// <summary>Relógio do servidor nos testes que dependem de hora (a cobertura só responde em horário comercial).</summary>
    public TimeProvider? Clock { get; init; }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(RecordingsPath, recursive: true);
        }
        catch (IOException)
        {
            // Pasta temporária; se ficar presa, o sistema limpa depois.
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // Arquivo temporário; se ficar preso, o sistema limpa depois.
            }
        }
    }

    /// <summary>Cliente do painel: guarda o cookie e manda o cabeçalho X-Pontual.</summary>
    public HttpClient Panel(bool withHeader = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (withHeader)
        {
            client.DefaultRequestHeaders.Add("X-Pontual", "1");
            // Reautenticação e motivo, para as ações que a matriz exige; todas as contas de teste usam a mesma senha.
            client.DefaultRequestHeaders.Add("X-Pontual-Senha", Uri.EscapeDataString(Password));
            client.DefaultRequestHeaders.Add("X-Pontual-Justificativa", Uri.EscapeDataString("teste automático"));
        }

        return client;
    }

    public HttpClient Terminal(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Faz a configuração inicial e devolve o painel já logado como superadministrador "Paulo".</summary>
    public async Task<HttpClient> OwnerAsync()
    {
        var client = Panel();
        var setup = await client.PostAsJsonAsync("/api/v1/setup", new { name = "Paulo", email = OwnerEmail, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, setup.StatusCode);
        await LoginAsync(client, OwnerEmail);
        return client;
    }

    public static async Task LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    /// <summary>Cria outro administrador (pelo superadministrador) e devolve um painel logado como ele.</summary>
    public async Task<HttpClient> AdminAsync(HttpClient owner, string name, string role)
    {
        var email = $"{name.ToLowerInvariant()}@pontual.test";
        var created = await owner.PostAsJsonAsync("/api/v1/admins", new { name, email, password = Password, role });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var client = Panel();
        await LoginAsync(client, email);
        return client;
    }

    /// <summary>Código → enroll. A máquina fica pendente.</summary>
    public async Task<(Guid DeviceId, string Token)> EnrollAsync(HttpClient admin, string hostname)
    {
        var code = await admin.PostAsync("/api/v1/enrollment-codes", null).Json();
        var enrolled = await CreateClient().PostAsJsonAsync("/api/v1/device/enroll",
            new { code = code.GetProperty("code").GetString(), hostname, windows = "Windows 10 Pro 22H2" });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        var body = await enrolled.Json();
        return (body.GetProperty("deviceId").GetGuid(), body.GetProperty("token").GetString()!);
    }

    /// <summary>Máquina cadastrada e aprovada, com o terminal já autenticado.</summary>
    public async Task<(Guid DeviceId, HttpClient Terminal)> ApprovedDeviceAsync(HttpClient admin, string hostname, string attendant)
    {
        var (deviceId, token) = await EnrollAsync(admin, hostname);
        var approved = await admin.PostAsJsonAsync($"/api/v1/devices/{deviceId}/approve", new { attendantName = attendant });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return (deviceId, Terminal(token));
    }
}

public static class HttpTestExtensions
{
    public static async Task<JsonElement> Json(this Task<HttpResponseMessage> pending) => await (await pending).Json();

    public static async Task<JsonElement> Json(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<HttpResponseMessage> SendEventsAsync(this HttpClient terminal, params object[] events) =>
        terminal.PostAsJsonAsync("/api/v1/device/events", new { events });

    public static async Task<string> ErrorAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;
}
