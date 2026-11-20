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
        Assert.Contains("Abriu www.youtube.com no Google Chrome, que não é um site operacional", await AlertsAsync(owner));
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

        // Depois de bloqueado, nova tentativa não gera alerta (só histórico).
        await terminal.SendEventsAsync(Visit("m.www.youtube.com"));
        Assert.DoesNotContain(await AlertsAsync(owner), t => t.Contains("youtube"));
        Assert.Single(await SitesAsync(owner));

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
