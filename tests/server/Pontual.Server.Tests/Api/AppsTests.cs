using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pontual.Server.Tests.Api;

/// <summary>Aplicativos do computador abertos pela lista do Terminal: na primeira vez vira pedido; o TI libera ou bloqueia.</summary>
public sealed class AppsTests
{
    private const string Spotify = @"C:\Users\gabi\AppData\Roaming\Spotify\Spotify.exe";

    private static object Opened(string name, string path) => new { type = "app_opened", item = name, detail = path, at = DateTime.UtcNow };

    private static async Task<JsonElement[]> AppsAsync(HttpClient admin) => [.. (await admin.GetAsync("/api/v1/device-apps").Json()).EnumerateArray()];

    private static async Task<string[]> AlertsAsync(HttpClient admin) =>
        [.. (await admin.GetAsync("/api/v1/alerts").Json()).EnumerateArray().Select(a => a.GetProperty("text").GetString()!)];

    private static Task<HttpResponseMessage> DecideAsync(HttpClient admin, string exe, string decision, Guid? deviceId = null) =>
        admin.PostAsJsonAsync("/api/v1/device-apps/decide", new { exe, decision, deviceId });

    private static string[] Strings(JsonElement config, string name) => [.. config.GetProperty(name).EnumerateArray().Select(h => h.GetString()!)];

    private static async Task<(string[] Allowed, string[] Blocked)> ListsAsync(HttpClient terminal)
    {
        var config = await terminal.GetAsync("/api/v1/device/config").Json();
        return (Strings(config, "appsAllowed"), Strings(config, "appsBlocked"));
    }

    [Fact]
    public async Task Aplicativo_aberto_pela_primeira_vez_vira_pedido_e_a_decisao_chega_ao_terminal()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Ana Souza");

        // Programa cadastrado para a máquina não pede nada; evento sem .exe é ignorado.
        const string excel = @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE";
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/items/programs", new { name = "Excel", path = excel, reason = "planilhas" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await terminal.SendEventsAsync(Opened("Excel", excel), Opened("Foto", @"C:\x\foto.png"))).StatusCode);
        Assert.Empty(await AppsAsync(owner));

        // Aplicativo novo: pedido dela, com nome, caminho, alerta e notificação urgente.
        await terminal.SendEventsAsync(Opened("Spotify", Spotify));
        var pedido = Assert.Single(await AppsAsync(owner));
        Assert.Equal("spotify.exe", pedido.GetProperty("exe").GetString());
        Assert.Equal("Spotify", pedido.GetProperty("name").GetString());
        Assert.Equal(Spotify, pedido.GetProperty("path").GetString());
        Assert.Equal("pending", pedido.GetProperty("status").GetString());
        Assert.Equal(1, pedido.GetProperty("opens").GetInt32());
        Assert.Contains("Ana Souza", pedido.GetProperty("deviceLabel").GetString());
        Assert.Contains("Abriu o aplicativo spotify.exe, que espera a decisão do TI", await AlertsAsync(owner));
        var urgent = (await owner.GetAsync("/api/v1/notifications").Json()).EnumerateArray().First();
        Assert.True(urgent.GetProperty("urgent").GetBoolean());
        Assert.Contains("Spotify (spotify.exe) pela primeira vez", urgent.GetProperty("text").GetString());

        // Abrir de novo soma vezes, sem segundo alerta.
        await terminal.SendEventsAsync(Opened("Spotify", Spotify));
        Assert.Equal(2, Assert.Single(await AppsAsync(owner)).GetProperty("opens").GetInt32());
        Assert.Single(await AlertsAsync(owner), t => t.Contains("spotify"));

        var before = (await terminal.GetAsync("/api/v1/device/config").Json()).GetProperty("version").GetInt64();

        // Bloquear para todas: o alerta é resolvido, o pedido some e a lista chega ao terminal com versão nova.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "spotify.exe", "block")).StatusCode);
        Assert.DoesNotContain(await AlertsAsync(owner), t => t.Contains("spotify"));
        var config = await terminal.GetAsync("/api/v1/device/config").Json();
        Assert.True(config.GetProperty("version").GetInt64() > before);
        Assert.Equal(["spotify.exe"], Strings(config, "appsBlocked"));
        var decidido = Assert.Single(await AppsAsync(owner));
        Assert.Equal("blocked", decidido.GetProperty("status").GetString());
        Assert.Equal("all", decidido.GetProperty("scope").GetString());
        Assert.Equal("Spotify", decidido.GetProperty("name").GetString());
        Assert.Equal("Paulo", decidido.GetProperty("decidedBy").GetString());

        // Bloqueado para todas, nova tentativa continua bloqueada, mas vira pedido dela (com alerta) para o TI decidir.
        await terminal.SendEventsAsync(Opened("Spotify", Spotify));
        Assert.Single(await AlertsAsync(owner), t => t.Contains("spotify"));
        pedido = Assert.Single(await AppsAsync(owner), a => a.GetProperty("status").GetString() == "pending");
        Assert.True(pedido.GetProperty("blockedForAll").GetBoolean());
        Assert.Contains("spotify.exe", (await ListsAsync(terminal)).Blocked);

        // Liberar troca de lista e apaga o pedido.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "spotify.exe", "allow")).StatusCode);
        var listas = await ListsAsync(terminal);
        Assert.Contains("spotify.exe", listas.Allowed);
        Assert.Empty(listas.Blocked);
        Assert.DoesNotContain(await AppsAsync(owner), a => a.GetProperty("status").GetString() == "pending");

        // Liberado, abrir de novo não gera pedido.
        await terminal.SendEventsAsync(Opened("Spotify", Spotify));
        Assert.Empty(await AlertsAsync(owner));

        // O TI também pode decidir um aplicativo que ainda não apareceu, pelo caminho ou pelo nome.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, @"C:\Jogos\Jogo.EXE", "block")).StatusCode);
        Assert.Contains("jogo.exe", (await ListsAsync(terminal)).Blocked);
    }

    [Fact]
    public async Task Cada_atendente_tem_o_proprio_pedido_e_a_decisao_dela_vale_mais_que_a_de_todas()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (gabiId, gabi) = await app.ApprovedDeviceAsync(owner, "PC-GABI", "Gabi");
        var (kamilaId, kamila) = await app.ApprovedDeviceAsync(owner, "PC-KAMILA", "Kamila");

        await gabi.SendEventsAsync(Opened("Spotify", Spotify));
        await kamila.SendEventsAsync(Opened("Spotify", @"C:\Users\kamila\AppData\Roaming\Spotify\Spotify.exe"));
        var pedidos = await AppsAsync(owner);
        Assert.Equal(2, pedidos.Length);
        Assert.All(pedidos, p => Assert.Equal("pending", p.GetProperty("status").GetString()));
        Assert.Equal(2, (await AlertsAsync(owner)).Count(t => t.Contains("spotify")));

        // A Gabi pode, a Kamila não. Cada decisão resolve só o alerta daquela atendente.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "spotify.exe", "allow", gabiId)).StatusCode);
        Assert.Single(await AlertsAsync(owner), t => t.Contains("spotify"));
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "spotify.exe", "block", kamilaId)).StatusCode);
        Assert.DoesNotContain(await AlertsAsync(owner), t => t.Contains("spotify"));
        Assert.Contains("spotify.exe", (await ListsAsync(gabi)).Allowed);
        Assert.Contains("spotify.exe", (await ListsAsync(kamila)).Blocked);

        // Bloqueada só para ela, a tentativa seguinte fica só no histórico.
        await kamila.SendEventsAsync(Opened("Spotify", Spotify));
        Assert.Empty(await AlertsAsync(owner));
        Assert.DoesNotContain(await AppsAsync(owner), a => a.GetProperty("status").GetString() == "pending");

        // Bloqueado para todas: a Gabi continua liberada (a decisão dela vale mais).
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "spotify.exe", "block")).StatusCode);
        Assert.Contains("spotify.exe", (await ListsAsync(gabi)).Allowed);
        Assert.DoesNotContain("spotify.exe", (await ListsAsync(gabi)).Blocked);

        // Tirar a decisão só da Gabi: volta a valer a de todas.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "spotify.exe", "reset", gabiId)).StatusCode);
        Assert.Contains("spotify.exe", (await ListsAsync(gabi)).Blocked);
        Assert.DoesNotContain(await AppsAsync(owner), a => a.GetProperty("scope").GetString() == "device" && a.GetProperty("deviceId").GetGuid() == gabiId);

        // "reset" só existe para uma atendente; máquina inexistente é 404.
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(owner, "spotify.exe", "reset")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(owner, "spotify.exe", "block", Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Decisao_recusa_entradas_invalidas_e_exige_permissao()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var support = await app.AdminAsync(owner, "Caio", "support");

        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(owner, "cmd.exe", "allow")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(owner, "spotify.exe", "talvez")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(owner, "foto.png", "block")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(owner, ".exe", "block")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(support, "spotify.exe", "block")).StatusCode);
        Assert.Empty(await AppsAsync(owner));

        // Bloquear o cmd.exe pode (é só reforço); liberar, nunca.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, "cmd.exe", "block")).StatusCode);
    }
}
