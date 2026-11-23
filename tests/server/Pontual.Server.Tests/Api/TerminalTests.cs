using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Data;

namespace Pontual.Server.Tests.Api;

public sealed class TerminalTests
{
    private static async Task<JsonElement> DeviceAsync(HttpClient admin, Guid id) =>
        (await admin.GetAsync("/api/v1/devices").Json()).EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == id);

    private static Task<JsonElement> HeartbeatAsync(HttpClient terminal, params string[] apps) =>
        terminal.PostAsJsonAsync("/api/v1/device/heartbeat", new { appsInUse = apps, sessionStartedAt = DateTime.UtcNow.AddMinutes(-3) }).Json();

    private static async Task<Guid> ItemIdAsync(HttpClient admin, string name) =>
        (await admin.GetAsync("/api/v1/items").Json()).EnumerateArray().Single(i => i.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    [Fact]
    public async Task Fluxo_completo_de_cadastro_de_maquina()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();

        // Código no formato do contrato, com validade de 30 minutos.
        var issued = await owner.PostAsync("/api/v1/enrollment-codes", null).Json();
        var code = issued.GetProperty("code").GetString()!;
        Assert.Matches(new Regex("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$"), code);
        var validity = issued.GetProperty("expiresAt").GetDateTime().ToUniversalTime() - DateTime.UtcNow;
        Assert.InRange(validity.TotalMinutes, 29, 30.1);

        var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/device/enroll",
            new { code = "AAAA-AAAA", hostname = "PC-04", windows = "Windows 10" })).StatusCode);

        var enrolled = await anonymous.PostAsJsonAsync("/api/v1/device/enroll", new { code, hostname = "PC-04", windows = "Windows 10 Pro 22H2" }).Json();
        var deviceId = enrolled.GetProperty("deviceId").GetGuid();
        var token = enrolled.GetProperty("token").GetString()!;

        // Uso único.
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/device/enroll",
            new { code, hostname = "PC-05", windows = "Windows 10" })).StatusCode);

        // O servidor guarda só o SHA-256 do token.
        using (var scope = app.Services.CreateScope())
        {
            var stored = scope.ServiceProvider.GetRequiredService<PontualDb>().Devices.Single(d => d.Id == deviceId);
            Assert.NotEqual(token, stored.TokenHash);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))), stored.TokenHash);
        }

        // Pendente, com o modo de observação ligado de nascença, e sem receber itens.
        var terminal = app.Terminal(token);
        var pendingBeat = await HeartbeatAsync(terminal);
        Assert.Equal("pending", pendingBeat.GetProperty("state").GetString());
        var pending = await DeviceAsync(owner, deviceId);
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.Equal("PC-04", pending.GetProperty("hostname").GetString());
        Assert.True(pending.GetProperty("observationMode").GetBoolean());
        Assert.Empty((await terminal.GetAsync("/api/v1/device/config").Json()).GetProperty("items").EnumerateArray());

        // Outra máquina, para conferir o público.
        var (otherId, otherTerminal) = await app.ApprovedDeviceAsync(owner, "PC-09", "Joana Lima");

        var approved = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/approve", new { attendantName = "Marina Alves" }).Json();
        Assert.Equal("Marina Alves", approved.GetProperty("attendantName").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/approve", new { attendantName = "Outra" })).StatusCode);

        var beat = await HeartbeatAsync(terminal, "Safra");
        Assert.Equal("active", beat.GetProperty("state").GetString());
        Assert.Equal("Marina Alves", beat.GetProperty("attendantName").GetString());
        Assert.True(beat.GetProperty("observationMode").GetBoolean());
        Assert.Equal(JsonValueKind.Null, beat.GetProperty("maintenance").ValueKind);

        var online = await DeviceAsync(owner, deviceId);
        Assert.Equal("online", online.GetProperty("state").GetString());
        Assert.Equal(["Safra"], online.GetProperty("appsInUse").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("Windows 10 Pro 22H2", online.GetProperty("windows").GetString());

        // Público: Safra só para a PC-09, Gmail só para a PC-04, Teams bloqueado, Digio para ninguém.
        await owner.PatchAsJsonAsync($"/api/v1/items/{await ItemIdAsync(owner, "Safra")}", new { audience = new[] { otherId } }).Json();
        await owner.PatchAsJsonAsync($"/api/v1/items/{await ItemIdAsync(owner, "Gmail")}", new { audience = new[] { deviceId } }).Json();
        await owner.PatchAsJsonAsync($"/api/v1/items/{await ItemIdAsync(owner, "Teams")}", new { enabled = false }).Json();
        await owner.PatchAsJsonAsync($"/api/v1/items/{await ItemIdAsync(owner, "Digio")}", new { audience = Array.Empty<Guid>() }).Json();

        var config = await terminal.GetAsync("/api/v1/device/config").Json();
        var names = config.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Equal(19, names.Count);
        Assert.DoesNotContain("Safra", names);
        Assert.DoesNotContain("Teams", names);
        Assert.DoesNotContain("Digio", names);
        Assert.Contains("Gmail", names);
        Assert.Equal("Marina Alves", config.GetProperty("attendantName").GetString());
        Assert.Equal(config.GetProperty("version").GetInt64(), (await HeartbeatAsync(terminal)).GetProperty("configVersion").GetInt64());
        Assert.True(config.GetProperty("version").GetInt64() > beat.GetProperty("configVersion").GetInt64());

        var gmail = config.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Gmail");
        Assert.Equal(["mail.google.com", "accounts.google.com"], gmail.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal("site", gmail.GetProperty("kind").GetString());
        Assert.Equal("Comunicação", gmail.GetProperty("group").GetString());
        var intern = config.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Sistema interno");
        Assert.Equal(["192.168.0.243"], intern.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));

        var otherNames = (await otherTerminal.GetAsync("/api/v1/device/config").Json())
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Contains("Safra", otherNames);
        Assert.DoesNotContain("Gmail", otherNames);
    }

    [Fact]
    public async Task Rotas_do_terminal_exigem_token_valido()
    {
        using var app = new TestApp();
        await app.OwnerAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/v1/device/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Terminal("token-inventado").GetAsync("/api/v1/device/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.Terminal("token-inventado").PostAsJsonAsync("/api/v1/device/heartbeat", new { appsInUse = Array.Empty<string>() })).StatusCode);
    }

    [Fact]
    public async Task Maquina_removida_recebe_401_no_heartbeat()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");
        Assert.Equal("active", (await HeartbeatAsync(terminal)).GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/devices/{deviceId}")).StatusCode);

        var beat = await terminal.PostAsJsonAsync("/api/v1/device/heartbeat", new { appsInUse = Array.Empty<string>(), sessionStartedAt = DateTime.UtcNow });
        Assert.Equal(HttpStatusCode.Unauthorized, beat.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await terminal.GetAsync("/api/v1/device/config")).StatusCode);
        Assert.Empty((await owner.GetAsync("/api/v1/devices").Json()).EnumerateArray());
    }

    [Fact]
    public async Task Evento_blocked_cria_alerta_e_notificacao_urgente()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");
        await HeartbeatAsync(terminal);
        await owner.PostAsync("/api/v1/notifications/read", null);

        var sent = await terminal.SendEventsAsync(new { type = "blocked", detail = "Jogos.Example.com", at = DateTime.UtcNow });
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        var alert = Assert.Single((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray());
        Assert.Equal(deviceId, alert.GetProperty("deviceId").GetGuid());
        Assert.Equal("Marina Alves (PC-04)", alert.GetProperty("deviceLabel").GetString());
        Assert.Contains("jogos.example.com", alert.GetProperty("text").GetString());

        var urgent = Assert.Single((await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray(), n => !n.GetProperty("read").GetBoolean());
        Assert.True(urgent.GetProperty("urgent").GetBoolean());
        Assert.Contains("Marina Alves (PC-04)", urgent.GetProperty("text").GetString());
        Assert.Contains("jogos.example.com", urgent.GetProperty("text").GetString());

        var device = await DeviceAsync(owner, deviceId);
        Assert.Equal("alert", device.GetProperty("state").GetString());
        Assert.Equal(1, device.GetProperty("openAlerts").GetInt32());

        var history = (await owner.GetAsync($"/api/v1/devices/{deviceId}/history").Json()).EnumerateArray().ToList();
        Assert.Contains(history, h => h.GetProperty("text").GetString()!.Contains("jogos.example.com"));

        // Resolver o alerta devolve a máquina a "online".
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/alerts/{alert.GetProperty("id").GetGuid()}/resolve", null)).StatusCode);
        Assert.Empty((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray());
        Assert.Equal("online", (await DeviceAsync(owner, deviceId)).GetProperty("state").GetString());

        await owner.PostAsync("/api/v1/notifications/read", null);
        Assert.All((await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray(), n => Assert.True(n.GetProperty("read").GetBoolean()));
    }

    [Fact]
    public async Task Evento_app_blocked_do_celular_cria_alerta_e_notificacao_urgente()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "CEL-SM-A075M", "Marina Alves");
        await HeartbeatAsync(terminal);
        await owner.PostAsync("/api/v1/notifications/read", null);

        var sent = await terminal.SendEventsAsync(new { type = "app_blocked", detail = "Configurações", at = DateTime.UtcNow });
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        var alert = Assert.Single((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray());
        Assert.Equal(deviceId, alert.GetProperty("deviceId").GetGuid());
        Assert.Contains("Configurações", alert.GetProperty("text").GetString());

        var urgent = Assert.Single((await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray(), n => !n.GetProperty("read").GetBoolean());
        Assert.True(urgent.GetProperty("urgent").GetBoolean());
        Assert.Contains("Marina Alves (CEL-SM-A075M)", urgent.GetProperty("text").GetString());
        Assert.Contains("Configurações", urgent.GetProperty("text").GetString());

        var history = (await owner.GetAsync($"/api/v1/devices/{deviceId}/history").Json()).EnumerateArray().ToList();
        Assert.Contains(history, h => h.GetProperty("text").GetString()!.Contains("aplicativo Configurações"));

        // Sem o nome do aplicativo, o evento é ignorado.
        await owner.PostAsync($"/api/v1/alerts/{alert.GetProperty("id").GetGuid()}/resolve", null);
        await terminal.SendEventsAsync(new { type = "app_blocked", detail = "  ", at = DateTime.UtcNow });
        Assert.Empty((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray());
    }

    [Fact]
    public async Task Aplicativos_da_Loja_comecam_desligados_e_o_TI_liga_por_computador()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "KAMILINHA", "Kamila");
        var (_, other) = await app.ApprovedDeviceAsync(owner, "PC-02", "Ana");

        Assert.False((await terminal.GetAsync("/api/v1/device/config").Json()).GetProperty("storeApps").GetBoolean());
        var device = await owner.PatchAsJsonAsync($"/api/v1/devices/{deviceId}", new { storeApps = true }).Json();
        Assert.True(device.GetProperty("storeApps").GetBoolean());
        Assert.True((await terminal.GetAsync("/api/v1/device/config").Json()).GetProperty("storeApps").GetBoolean());
        Assert.False((await other.GetAsync("/api/v1/device/config").Json()).GetProperty("storeApps").GetBoolean());

        Assert.Contains("ligou os aplicativos da Microsoft Store em Kamila (KAMILINHA)", (await owner.GetAsync("/api/v1/audit").Json()).GetRawText());

        await terminal.SendEventsAsync(new { type = "store_shell", item = "Microsoft Store", detail = "o Explorer está rodando escondido", at = DateTime.UtcNow });
        var history = (await owner.GetAsync($"/api/v1/devices/{deviceId}/history").Json()).EnumerateArray();
        Assert.Contains(history, h => h.GetProperty("text").GetString() == "Aplicativos da Microsoft Store: o Explorer está rodando escondido");
    }

    [Fact]
    public async Task Evento_app_failed_vai_para_o_historico_com_o_motivo_e_cria_um_alerta()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-05", "Ana");

        await terminal.SendEventsAsync(
            new { type = "app_failed", item = "Notas Autoadesivas", detail = "o aplicativo não está instalado nesta conta do Windows", at = DateTime.UtcNow },
            new { type = "app_failed", item = "Notas Autoadesivas", detail = "de novo", at = DateTime.UtcNow });

        var history = (await owner.GetAsync($"/api/v1/devices/{deviceId}/history").Json()).EnumerateArray().ToList();
        Assert.Contains(history, h => h.GetProperty("text").GetString() == "Não conseguiu abrir Notas Autoadesivas: o aplicativo não está instalado nesta conta do Windows");
        var alert = Assert.Single((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray());
        Assert.Contains("Notas Autoadesivas", alert.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Evento_opened_cria_notificacao_nao_urgente_e_historico()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");
        await owner.PostAsync("/api/v1/notifications/read", null);

        await terminal.SendEventsAsync(
            new { type = "session_started", at = DateTime.UtcNow.AddSeconds(-30) },
            new { type = "opened", item = "Safra", at = DateTime.UtcNow.AddSeconds(-20) },
            new { type = "closed", item = "Safra", at = DateTime.UtcNow.AddSeconds(-10) });

        var notification = Assert.Single((await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray(), n => !n.GetProperty("read").GetBoolean());
        Assert.False(notification.GetProperty("urgent").GetBoolean());
        Assert.Equal("Marina Alves (PC-04) abriu Safra", notification.GetProperty("text").GetString());
        Assert.Empty((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray());

        var history = (await owner.GetAsync($"/api/v1/devices/{deviceId}/history").Json()).EnumerateArray().Select(h => h.GetProperty("text").GetString()).ToList();
        Assert.Equal(["Fechou Safra", "Abriu Safra", "Abriu o terminal"], history);
    }

    [Fact]
    public async Task Evento_help_cria_alerta_e_notificacao_nao_urgente()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");
        await owner.PostAsync("/api/v1/notifications/read", null);

        await terminal.SendEventsAsync(new { type = "help", at = DateTime.UtcNow });

        Assert.Equal("Chamou o TI", Assert.Single((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray()).GetProperty("text").GetString());
        var notification = Assert.Single((await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray(), n => !n.GetProperty("read").GetBoolean());
        Assert.False(notification.GetProperty("urgent").GetBoolean());
    }

    [Fact]
    public async Task Evento_observed_entra_na_lista_e_allow_move_o_dominio_para_o_item()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");
        var parana = await ItemIdAsync(owner, "Paraná Banco");
        await owner.PostAsync("/api/v1/notifications/read", null);
        var versionBefore = (await HeartbeatAsync(terminal)).GetProperty("configVersion").GetInt64();

        await terminal.SendEventsAsync(
            new { type = "observed", item = "Paraná Banco", detail = "api.paranabanco.com.br", at = DateTime.UtcNow },
            new { type = "observed", item = "Paraná Banco", detail = "api.paranabanco.com.br", at = DateTime.UtcNow },
            // Subdomínio de um domínio já liberado não é novidade.
            new { type = "observed", item = "Paraná Banco", detail = "cdn.front.paranabanco.b.br", at = DateTime.UtcNow });

        var observed = Assert.Single((await owner.GetAsync("/api/v1/observed-hosts").Json()).EnumerateArray());
        Assert.Equal("api.paranabanco.com.br", observed.GetProperty("host").GetString());
        Assert.Equal(parana, observed.GetProperty("itemId").GetGuid());
        Assert.Equal("Paraná Banco", observed.GetProperty("itemName").GetString());
        Assert.Equal("Marina Alves (PC-04)", observed.GetProperty("deviceLabel").GetString());
        Assert.Equal(2, observed.GetProperty("count").GetInt32());

        // Notificação não urgente só na primeira vez do domínio.
        var notification = Assert.Single((await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray(), n => !n.GetProperty("read").GetBoolean());
        Assert.False(notification.GetProperty("urgent").GetBoolean());
        Assert.Contains("api.paranabanco.com.br", notification.GetProperty("text").GetString());
        Assert.Empty((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray());

        var allowed = await owner.PostAsJsonAsync("/api/v1/observed-hosts/allow", new { host = "api.paranabanco.com.br", itemId = parana });
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);

        Assert.Empty((await owner.GetAsync("/api/v1/observed-hosts").Json()).EnumerateArray());
        var item = (await owner.GetAsync("/api/v1/items").Json()).EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == parana);
        Assert.Equal(["api.paranabanco.com.br"], item.GetProperty("extraHosts").EnumerateArray().Select(h => h.GetString()));

        var config = await terminal.GetAsync("/api/v1/device/config").Json();
        var hosts = config.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == parana).GetProperty("hosts");
        Assert.Equal(["front.paranabanco.b.br", "api.paranabanco.com.br"], hosts.EnumerateArray().Select(h => h.GetString()));
        Assert.True(config.GetProperty("version").GetInt64() > versionBefore);
    }

    [Fact]
    public async Task Dados_ruins_do_terminal_sao_ignorados_ou_recusados()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");
        await owner.PostAsync("/api/v1/notifications/read", null);

        var sent = await terminal.SendEventsAsync(
            new { type = "desconhecido", at = DateTime.UtcNow },
            new { type = "blocked", detail = "<script>alert(1)</script>", at = DateTime.UtcNow },
            new { type = "observed", detail = "não é domínio", at = DateTime.UtcNow },
            new { type = "opened", item = "", at = DateTime.UtcNow },
            new { type = "opened", item = new string('x', 5000), at = "data inválida" });
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        Assert.Empty((await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray());
        Assert.Empty((await owner.GetAsync("/api/v1/observed-hosts").Json()).EnumerateArray());
        var history = Assert.Single((await owner.GetAsync($"/api/v1/devices/{deviceId}/history").Json()).EnumerateArray());
        Assert.True(history.GetProperty("text").GetString()!.Length <= 80);

        // Lote acima do limite e corpo que não é JSON são recusados.
        var tooMany = await terminal.SendEventsAsync([.. Enumerable.Range(0, 101).Select(_ => (object)new { type = "closed", item = "Safra" })]);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        var notJson = await terminal.PostAsync("/api/v1/device/events", new StringContent("{ isto não é json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await notJson.ErrorAsync()));
    }
}
