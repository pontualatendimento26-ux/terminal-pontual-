using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Pontual.Server.Tests.Api;

/// <summary>Servidor de verdade em memória, com um banco SQLite em arquivo temporário só deste teste.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string OwnerEmail = "paulo@pontual.test";
    public const string Password = "senha-de-teste-123";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pontual-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Path", _databasePath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
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
