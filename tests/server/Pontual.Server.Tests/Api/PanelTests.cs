using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Data;

namespace Pontual.Server.Tests.Api;

public sealed class PanelTests
{
    [Fact]
    public async Task Health_e_raiz()
    {
        using var app = new TestApp();
        var client = app.Panel();

        var health = await client.GetAsync("/api/v1/health").Json();
        Assert.True(health.GetProperty("ok").GetBoolean());
        Assert.Equal("1.0.0", health.GetProperty("version").GetString());

        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/central/", root.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Configuracao_inicial_so_funciona_uma_vez()
    {
        using var app = new TestApp();
        var client = app.Panel();

        Assert.True((await client.GetAsync("/api/v1/setup/status").Json()).GetProperty("needsSetup").GetBoolean());

        var first = await client.PostAsJsonAsync("/api/v1/setup", new { name = "Paulo", email = TestApp.OwnerEmail, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.False((await client.GetAsync("/api/v1/setup/status").Json()).GetProperty("needsSetup").GetBoolean());

        var second = await client.PostAsJsonAsync("/api/v1/setup", new { name = "Outro", email = "outro@pontual.test", password = TestApp.Password });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await second.ErrorAsync()));
    }

    [Fact]
    public async Task Configuracao_inicial_recusa_senha_curta()
    {
        using var app = new TestApp();
        var response = await app.Panel().PostAsJsonAsync("/api/v1/setup", new { name = "Paulo", email = TestApp.OwnerEmail, password = "curta" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_devolve_cookie_e_me_lista_permissoes()
    {
        using var app = new TestApp();
        var client = app.Panel();
        await client.PostAsJsonAsync("/api/v1/setup", new { name = "Paulo", email = TestApp.OwnerEmail, password = TestApp.Password });

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = TestApp.OwnerEmail, password = "senha-errada-000" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "PAULO@pontual.test", password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("pontual_admin=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var body = await login.Json();
        Assert.Equal("superadmin", body.GetProperty("role").GetString());
        Assert.Equal("Paulo", body.GetProperty("name").GetString());

        var me = await client.GetAsync("/api/v1/auth/me").Json();
        Assert.Equal(TestApp.OwnerEmail, me.GetProperty("email").GetString());
        Assert.Contains("admins.manage", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Sem_cookie_devolve_401()
    {
        using var app = new TestApp();
        await app.OwnerAsync();
        var anonymous = app.Panel();

        foreach (var route in new[] { "/api/v1/devices", "/api/v1/items", "/api/v1/alerts", "/api/v1/audit", "/api/v1/auth/me", "/api/v1/admins" })
        {
            var response = await anonymous.GetAsync(route);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(await response.ErrorAsync()));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/enrollment-codes", null)).StatusCode);
    }

    [Fact]
    public async Task Post_sem_cabecalho_X_Pontual_devolve_400()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();

        // Mesmo logado: o cookie é reaproveitado num cliente que não manda o cabeçalho.
        var cookie = (await owner.PostAsJsonAsync("/api/v1/auth/login", new { email = TestApp.OwnerEmail, password = TestApp.Password }))
            .Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var bare = app.Panel(withHeader: false);
        bare.DefaultRequestHeaders.Add("Cookie", cookie);

        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bare.PostAsync("/api/v1/enrollment-codes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bare.PostAsync("/api/v1/notifications/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bare.DeleteAsync($"/api/v1/devices/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await bare.PostAsJsonAsync("/api/v1/auth/login", new { email = TestApp.OwnerEmail, password = TestApp.Password })).StatusCode);
    }

    [Fact]
    public async Task Funcao_sem_permissao_devolve_403()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var auditor = await app.AdminAsync(owner, "Ana", "auditor");
        var support = await app.AdminAsync(owner, "Caio", "support");
        var admin = await app.AdminAsync(owner, "Bia", "admin");

        // Auditor lê, mas não altera.
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsync("/api/v1/enrollment-codes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/v1/admins")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/v1/items/sites",
            new { name = "X", url = "https://x.example.com", group = "Comunicação" })).StatusCode);

        // Suporte não lê auditoria nem mexe em sites; a permissão do PATCH depende do tipo do item.
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync("/api/v1/audit")).StatusCode);
        var safra = (await owner.GetAsync("/api/v1/items").Json()).EnumerateArray().First(i => i.GetProperty("name").GetString() == "Safra");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await support.PatchAsJsonAsync($"/api/v1/items/{safra.GetProperty("id").GetGuid()}", new { enabled = false })).StatusCode);

        // Administrador comum não gerencia administradores.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/admins",
            new { name = "Zé", email = "ze@pontual.test", password = TestApp.Password, role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/v1/enrollment-codes", null)).StatusCode);
    }

    [Fact]
    public async Task Carga_inicial_tem_os_22_sites_e_nao_tem_o_Excel()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var items = (await owner.GetAsync("/api/v1/items").Json()).EnumerateArray().ToList();

        Assert.Equal(22, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal("site", i.GetProperty("kind").GetString());
            Assert.Equal("active", i.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, i.GetProperty("audience").ValueKind);
        });
        Assert.DoesNotContain(items, i => i.GetProperty("name").GetString()!.Contains("Excel", StringComparison.OrdinalIgnoreCase));

        JsonElement Named(string name) => items.Single(i => i.GetProperty("name").GetString() == name);
        Assert.Equal("https://epfweb.safra.com.br/Home/Login", Named("Safra").GetProperty("target").GetString());
        Assert.Equal("http://192.168.0.243:3000/html/login.html", Named("Sistema interno").GetProperty("target").GetString());
        Assert.Equal(["accounts.google.com"], Named("Gmail").GetProperty("extraHosts").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(["login.microsoftonline.com", "login.live.com"], Named("Teams").GetProperty("extraHosts").EnumerateArray().Select(h => h.GetString()));
    }

    [Fact]
    public async Task Site_so_aceita_http_e_https()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();

        foreach (var url in new[] { "ftp://arquivos.example.com", "javascript:alert(1)", "file:///c:/windows", "sem-esquema.com", "" })
        {
            var response = await owner.PostAsJsonAsync("/api/v1/items/sites", new { name = "Ruim", url, group = "Comunicação" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var ok = await owner.PostAsJsonAsync("/api/v1/items/sites",
            new { name = "Novo banco", url = "https://portal.novobanco.example/login", group = "Bancos e promotoras", extraHosts = new[] { "Login.NovoBanco.example" } }).Json();
        Assert.Equal("active", ok.GetProperty("status").GetString());
        Assert.Equal(["login.novobanco.example"], ok.GetProperty("extraHosts").EnumerateArray().Select(h => h.GetString()));
    }

    [Fact]
    public async Task Programa_nao_pode_ser_aprovado_por_quem_propos()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var other = await app.AdminAsync(owner, "Bia", "admin");
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");

        var proposed = await owner.PostAsJsonAsync("/api/v1/items/programs", new
        {
            name = "Excel",
            path = @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
            reason = "Planilhas de comissão",
            audience = (Guid[]?)null,
        }).Json();
        var id = proposed.GetProperty("id").GetGuid();
        Assert.Equal("pending", proposed.GetProperty("status").GetString());
        Assert.Equal("program", proposed.GetProperty("kind").GetString());
        Assert.Equal("Paulo", proposed.GetProperty("proposal").GetProperty("by").GetString());
        Assert.Equal("Planilhas de comissão", proposed.GetProperty("proposal").GetProperty("reason").GetString());

        // Pendente não chega ao terminal.
        var before = await terminal.GetAsync("/api/v1/device/config").Json();
        Assert.DoesNotContain(before.GetProperty("items").EnumerateArray(), i => i.GetProperty("name").GetString() == "Excel");

        var self = await owner.PostAsync($"/api/v1/items/{id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);

        var approved = await other.PostAsync($"/api/v1/items/{id}/approve", null).Json();
        Assert.Equal("active", approved.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, approved.GetProperty("proposal").ValueKind);

        var after = await terminal.GetAsync("/api/v1/device/config").Json();
        var excel = after.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Excel");
        Assert.Equal("program", excel.GetProperty("kind").GetString());
        Assert.Empty(excel.GetProperty("hosts").EnumerateArray());
        Assert.True(after.GetProperty("version").GetInt64() > before.GetProperty("version").GetInt64());

        // Em programa só se muda bloqueio e público.
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PatchAsJsonAsync($"/api/v1/items/{id}", new { name = "Outro" })).StatusCode);
        var blocked = await other.PatchAsJsonAsync($"/api/v1/items/{id}", new { enabled = false, audience = new[] { deviceId } }).Json();
        Assert.Equal("blocked", blocked.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Windows\System32\CMD.EXE")]
    [InlineData("pwsh.exe")]
    [InlineData("C:/Windows/System32/mshta.exe")]
    [InlineData(@"C:\Windows\regedit.exe")]
    [InlineData(@"C:\Windows\System32\wscript.exe")]
    [InlineData(@"C:\Windows\System32\cmd .exe")]
    [InlineData(@"C:\Ferramentas\script.bat")]
    [InlineData(@"C:\Ferramentas\planilha.exe.lnk")]
    [InlineData(@"C:\Ferramentas\app.exe --arg")]
    public async Task Programa_recusado_devolve_400(string path)
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();

        var response = await owner.PostAsJsonAsync("/api/v1/items/programs", new { name = "Ferramenta", path, reason = "Teste" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var items = await owner.GetAsync("/api/v1/items").Json();
        Assert.DoesNotContain(items.EnumerateArray(), i => i.GetProperty("kind").GetString() == "program");
    }

    [Fact]
    public async Task Proposta_recusada_some_da_lista()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var proposed = await owner.PostAsJsonAsync("/api/v1/items/programs",
            new { name = "Calculadora", path = @"C:\Windows\System32\calc.exe", reason = "Contas" }).Json();
        var id = proposed.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/items/{id}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/v1/items/{id}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Manutencao_exige_motivo_e_prazo_valido()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");
        var route = $"/api/v1/devices/{deviceId}/maintenance";

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(route, new { reason = "", minutes = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(route, new { reason = "Troca de teclado", minutes = 4 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(route, new { reason = "Troca de teclado", minutes = 481 })).StatusCode);

        var started = await owner.PostAsJsonAsync(route, new { reason = "Troca de teclado", minutes = 30 }).Json();
        Assert.Equal("maintenance", started.GetProperty("state").GetString());
        Assert.Equal("Troca de teclado", started.GetProperty("maintenance").GetProperty("reason").GetString());

        var beat = await terminal.PostAsJsonAsync("/api/v1/device/heartbeat", new { appsInUse = Array.Empty<string>(), sessionStartedAt = DateTime.UtcNow }).Json();
        Assert.Equal("maintenance", beat.GetProperty("state").GetString());
        Assert.Equal("Troca de teclado", beat.GetProperty("maintenance").GetProperty("reason").GetString());

        var ended = await owner.DeleteAsync(route).Json();
        Assert.Equal("online", ended.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, ended.GetProperty("maintenance").ValueKind);
    }

    [Fact]
    public async Task Auditoria_fica_integra_apos_varias_acoes_e_acusa_adulteracao()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var other = await app.AdminAsync(owner, "Bia", "admin");
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-04", "Marina Alves");
        var safra = (await owner.GetAsync("/api/v1/items").Json()).EnumerateArray().First(i => i.GetProperty("name").GetString() == "Safra").GetProperty("id").GetGuid();

        await owner.PatchAsJsonAsync($"/api/v1/items/{safra}", new { audience = Array.Empty<Guid>() }).Json();
        await owner.PatchAsJsonAsync($"/api/v1/items/{safra}", new { audience = new[] { deviceId } }).Json();
        await owner.PatchAsJsonAsync($"/api/v1/devices/{deviceId}", new { observationMode = false }).Json();
        await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/maintenance", new { reason = "Limpeza", minutes = 10 }).Json();
        await terminal.SendEventsAsync(new { type = "help", at = DateTime.UtcNow });
        var alert = (await owner.GetAsync("/api/v1/alerts").Json()).EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await other.PostAsync($"/api/v1/alerts/{alert}/resolve", null)).StatusCode);

        // Ações simultâneas não podem ramificar a cadeia.
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? owner : other).PostAsync("/api/v1/enrollment-codes", null)));

        var verify = await owner.GetAsync("/api/v1/audit/verify").Json();
        Assert.True(verify.GetProperty("intact").GetBoolean());
        Assert.Equal(JsonValueKind.Null, verify.GetProperty("failedSequence").ValueKind);
        Assert.Equal(JsonValueKind.Null, verify.GetProperty("failure").ValueKind);

        var audit = (await owner.GetAsync("/api/v1/audit").Json()).EnumerateArray().ToList();
        var texts = audit.Select(a => a.GetProperty("text").GetString()).ToList();
        Assert.Contains("Paulo liberou Safra para Marina Alves (PC-04)", texts);
        Assert.Contains("Paulo desligou o modo de observação de Marina Alves (PC-04)", texts);
        Assert.Contains("Bia resolveu o alerta de Marina Alves (PC-04): Chamou o TI", texts);
        Assert.Contains(audit, a => a.GetProperty("actor").GetString() == "Bia");
        var sequences = audit.Select(a => a.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal(sequences.OrderByDescending(s => s), sequences);
        Assert.Equal(sequences.Count, sequences.Distinct().Count());

        // Alterar um texto direto no banco tem de aparecer na verificação.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            var record = db.Audit.Single(a => a.Sequence == 3);
            record.Text = "Texto adulterado";
            db.SaveChanges();
        }

        var broken = await owner.GetAsync("/api/v1/audit/verify").Json();
        Assert.False(broken.GetProperty("intact").GetBoolean());
        Assert.Equal(3, broken.GetProperty("failedSequence").GetInt64());
        Assert.Equal("hashMismatch", broken.GetProperty("failure").GetString());
    }
}
