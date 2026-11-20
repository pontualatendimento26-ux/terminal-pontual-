using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pontual.Server.Tests.Api;

/// <summary>Sites abertos no Google Chrome fora da lista: o Terminal avisa, o TI libera ou bloqueia.</summary>
public sealed class ChromeTests
{
    private static object Visit(string host) => new { type = "chrome_visit", item = "Google Chrome", detail = host, at = DateTime.UtcNow };
    private static object Stay(string host) => new { type = "chrome_stay", item = "Google Chrome", detail = host, at = DateTime.UtcNow };

    private static async Task<JsonElement[]> SitesAsync(HttpClient admin) => [.. (await admin.GetAsync("/api/v1/chrome-sites").Json()).EnumerateArray()];

    private static async Task<string[]> AlertsAsync(HttpClient admin) =>
        [.. (await admin.GetAsync("/api/v1/alerts").Json()).EnumerateArray().Select(a => a.GetProperty("text").GetString()!)];

    private static Task<HttpResponseMessage> DecideAsync(HttpClient admin, string host, string decision) =>
        admin.PostAsJsonAsync("/api/v1/chrome-sites/decide", new { host, decision });

    private static string[] Strings(JsonElement config, string name) => [.. config.GetProperty(name).EnumerateArray().Select(h => h.GetString()!)];

    [Fact]
    public async Task Site_nao_operacional_no_Chrome_avisa_o_TI_e_a_decisao_chega_ao_terminal()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Ana Souza");

        // Site operacional (WhatsApp, da lista inicial), login do Google e um não operacional.
        Assert.Equal(HttpStatusCode.NoContent, (await terminal.SendEventsAsync(
            Visit("web.whatsapp.com"), Visit("accounts.google.com"), Visit("www.youtube.com"), Visit("bolo"))).StatusCode);

        var site = Assert.Single(await SitesAsync(owner));
        Assert.Equal("www.youtube.com", site.GetProperty("host").GetString());
        Assert.Equal("pending", site.GetProperty("status").GetString());
        Assert.Equal(1, site.GetProperty("visits").GetInt32());
        Assert.Contains("Ana Souza", site.GetProperty("deviceLabel").GetString());
        Assert.Contains("Abriu www.youtube.com num navegador, que não é um site operacional", await AlertsAsync(owner));
        var urgent = (await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray().First();
        Assert.True(urgent.GetProperty("urgent").GetBoolean());

        // Continuar na tela soma minutos; voltar ao site soma visitas sem abrir outro alerta.
        await terminal.SendEventsAsync(Stay("www.youtube.com"), Stay("www.youtube.com"), Visit("www.youtube.com"));
        site = Assert.Single(await SitesAsync(owner));
        Assert.Equal(2, site.GetProperty("minutes").GetInt32());
        Assert.Equal(2, site.GetProperty("visits").GetInt32());
        Assert.Single(await AlertsAsync(owner), t => t.Contains("youtube"));

        var before = (await terminal.GetAsync("/api/v1/device/config").Json()).GetProperty("version").GetInt64();
        Assert.Contains("accounts.google.com", Strings(await terminal.GetAsync("/api/v1/device/config").Json(), "chromeAllowed"));

        // Bloquear: o alerta é resolvido e a lista chega ao terminal com versão nova.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "www.youtube.com", "block")).StatusCode);
        Assert.DoesNotContain(await AlertsAsync(owner), t => t.Contains("youtube"));
        var config = await terminal.GetAsync("/api/v1/device/config").Json();
        Assert.True(config.GetProperty("version").GetInt64() > before);
        Assert.Equal(["www.youtube.com"], Strings(config, "chromeBlocked"));
        site = Assert.Single(await SitesAsync(owner));
        Assert.Equal("blocked", site.GetProperty("status").GetString());
        Assert.Equal("Paulo", site.GetProperty("decidedBy").GetString());

        // Bloqueado para todas, nova tentativa continua bloqueada, mas vira pedido dela (com alerta) para o TI decidir.
        await terminal.SendEventsAsync(Visit("m.www.youtube.com"));
        Assert.Single(await AlertsAsync(owner), t => t.Contains("m.www.youtube.com"));
        var pedido = Assert.Single(await SitesAsync(owner), s => s.GetProperty("status").GetString() == "pending");
        Assert.Equal("m.www.youtube.com", pedido.GetProperty("host").GetString());
        Assert.True(pedido.GetProperty("blockedForAll").GetBoolean());
        Assert.Contains("www.youtube.com", Strings(await terminal.GetAsync("/api/v1/device/config").Json(), "chromeBlocked"));

        // Liberar troca de lista.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "www.youtube.com", "allow")).StatusCode);
        config = await terminal.GetAsync("/api/v1/device/config").Json();
        Assert.Contains("www.youtube.com", Strings(config, "chromeAllowed"));
        Assert.Empty(Strings(config, "chromeBlocked"));

        // O TI também pode decidir um domínio que ainda não apareceu.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "https://jogos.exemplo.com/x", "block")).StatusCode);
        Assert.Contains("jogos.exemplo.com", Strings(await terminal.GetAsync("/api/v1/device/config").Json(), "chromeBlocked"));
    }

    [Fact]
    public async Task Outros_navegadores_avisam_com_o_nome_deles_e_dividem_o_mesmo_alerta()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Ana Souza");

        object Visita(string browser, string host) => new { type = "chrome_visit", item = browser, detail = host, at = DateTime.UtcNow };

        await terminal.SendEventsAsync(Visita("Mozilla Firefox", "jogos.exemplo.com"));
        var notificacao = (await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray().First();
        Assert.Contains("jogos.exemplo.com no Mozilla Firefox", notificacao.GetProperty("text").GetString());

        // O mesmo site em outro navegador soma visita, sem abrir um segundo alerta.
        await terminal.SendEventsAsync(Visita("Opera", "jogos.exemplo.com"));
        Assert.Equal(2, Assert.Single(await SitesAsync(owner)).GetProperty("visits").GetInt32());
        Assert.Single(await AlertsAsync(owner), t => t.Contains("jogos.exemplo.com"));

        // Bloqueado para todas, o alerta é resolvido. Bloqueado só para ela, a tentativa seguinte fica só no histórico.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "jogos.exemplo.com", "block")).StatusCode);
        Assert.DoesNotContain(await AlertsAsync(owner), t => t.Contains("jogos.exemplo.com"));
        var (deviceId, _) = (Assert.Single((await owner.GetAsync("/api/v1/devices").Json()).EnumerateArray()).GetProperty("id").GetGuid(), 0);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/chrome-sites/decide", new { host = "jogos.exemplo.com", decision = "block", deviceId })).StatusCode);
        await terminal.SendEventsAsync(Visita("Microsoft Edge", "jogos.exemplo.com"));
        Assert.DoesNotContain(await AlertsAsync(owner), t => t.Contains("jogos.exemplo.com"));
        Assert.DoesNotContain(await SitesAsync(owner), s => s.GetProperty("status").GetString() == "pending");
    }

    [Fact]
    public async Task Cada_atendente_tem_o_proprio_pedido_e_a_decisao_dela_vale_mais_que_a_de_todas()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (gabiId, gabi) = await app.ApprovedDeviceAsync(owner, "PC-GABI", "Gabi");
        var (kamilaId, kamila) = await app.ApprovedDeviceAsync(owner, "PC-KAMILA", "Kamila");

        object Visita(string browser, string host) => new { type = "chrome_visit", item = browser, detail = host, at = DateTime.UtcNow };
        Task<HttpResponseMessage> Decidir(string host, string decision, Guid? deviceId) =>
            owner.PostAsJsonAsync("/api/v1/chrome-sites/decide", new { host, decision, deviceId });
        async Task<(string[] Allowed, string[] Blocked)> Listas(HttpClient terminal)
        {
            var config = await terminal.GetAsync("/api/v1/device/config").Json();
            return (Strings(config, "chromeAllowed"), Strings(config, "chromeBlocked"));
        }

        // As duas abrem o YouTube: um pedido para cada uma, com o navegador.
        await gabi.SendEventsAsync(Visita("Google Chrome", "youtube.com"));
        await kamila.SendEventsAsync(Visita("Mozilla Firefox", "youtube.com"));
        var pedidos = await SitesAsync(owner);
        Assert.Equal(2, pedidos.Length);
        Assert.All(pedidos, p => Assert.Equal("pending", p.GetProperty("status").GetString()));
        Assert.Contains(pedidos, p => p.GetProperty("deviceId").GetGuid() == kamilaId && p.GetProperty("browser").GetString() == "Mozilla Firefox");
        Assert.Equal(2, (await AlertsAsync(owner)).Count(t => t.Contains("youtube.com")));

        // A Gabi pode, a Kamila não. Cada decisão resolve só o alerta daquela atendente.
        Assert.Equal(HttpStatusCode.OK, (await Decidir("youtube.com", "allow", gabiId)).StatusCode);
        Assert.Single(await AlertsAsync(owner), t => t.Contains("youtube.com"));
        Assert.Equal(HttpStatusCode.OK, (await Decidir("youtube.com", "block", kamilaId)).StatusCode);
        Assert.DoesNotContain(await AlertsAsync(owner), t => t.Contains("youtube.com"));
        Assert.Contains("youtube.com", (await Listas(gabi)).Allowed);
        Assert.DoesNotContain("youtube.com", (await Listas(gabi)).Blocked);
        Assert.Contains("youtube.com", (await Listas(kamila)).Blocked);

        // Bloqueado para todas: a Gabi continua liberada (a decisão dela vale mais).
        Assert.Equal(HttpStatusCode.OK, (await Decidir("youtube.com", "block", null)).StatusCode);
        Assert.DoesNotContain("youtube.com", (await Listas(gabi)).Blocked);
        Assert.Contains("youtube.com", (await Listas(kamila)).Blocked);

        // Tirar a decisão só da Gabi: volta a valer a de todas.
        Assert.Equal(HttpStatusCode.OK, (await Decidir("youtube.com", "reset", gabiId)).StatusCode);
        Assert.Contains("youtube.com", (await Listas(gabi)).Blocked);
        Assert.DoesNotContain(await SitesAsync(owner), s => s.GetProperty("scope").GetString() == "device" && s.GetProperty("deviceId").GetGuid() == gabiId);

        // Site novo da Gabi decidido "para todas": o pedido dela some e a decisão vale para a Kamila também.
        await gabi.SendEventsAsync(Visita("Opera", "jogos.exemplo.com"));
        Assert.Contains(await SitesAsync(owner), s => s.GetProperty("host").GetString() == "jogos.exemplo.com" && s.GetProperty("status").GetString() == "pending");
        Assert.Equal(HttpStatusCode.OK, (await Decidir("jogos.exemplo.com", "block", null)).StatusCode);
        Assert.DoesNotContain(await SitesAsync(owner), s => s.GetProperty("host").GetString() == "jogos.exemplo.com" && s.GetProperty("status").GetString() == "pending");
        Assert.Contains("jogos.exemplo.com", (await Listas(kamila)).Blocked);

        // "reset" só existe para uma atendente.
        Assert.Equal(HttpStatusCode.BadRequest, (await Decidir("jogos.exemplo.com", "reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Decidir("jogos.exemplo.com", "block", Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Decisao_recusa_entradas_invalidas_e_exige_permissao()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var support = await app.AdminAsync(owner, "Caio", "support");

        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(owner, "accounts.google.com", "block")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(owner, "exemplo.com", "talvez")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(owner, "sem-ponto", "block")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(support, "exemplo.com", "block")).StatusCode);
        Assert.Empty(await SitesAsync(owner));
    }
}
