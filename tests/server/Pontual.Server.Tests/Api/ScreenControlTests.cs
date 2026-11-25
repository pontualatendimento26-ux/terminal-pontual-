using System.Net;
using System.Net.Http.Json;

namespace Pontual.Server.Tests.Api;

public sealed class ScreenControlTests
{
    [Fact]
    public async Task Admin_assume_o_controle_e_o_terminal_recebe_os_comandos()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Bianca");

        // Sem ninguém vendo a tela, o terminal não deve receber controle.
        var parado = await terminal.GetAsync("/api/v1/device/control").Json();
        Assert.False(parado.GetProperty("controlling").GetBoolean());

        // O admin abre a tela e assume o controle.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/devices/{deviceId}/screen", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/devices/{deviceId}/control", null)).StatusCode);

        // Manda alguns comandos — exatamente como o painel manda (um array JSON no corpo).
        var comandos = new object[]
        {
            new { type = "move", x = 0.5, y = 0.5 },
            new { type = "down", button = 0 },
            new { type = "up", button = 0 },
            new { type = "text", text = "oi" },
        };
        var enviou = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/control/input", comandos);
        Assert.Equal(HttpStatusCode.NoContent, enviou.StatusCode);

        // O terminal busca e deve receber os comandos, com controlling verdadeiro.
        var lote = await terminal.GetAsync("/api/v1/device/control").Json();
        Assert.True(lote.GetProperty("controlling").GetBoolean());
        var eventos = lote.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(4, eventos.Length);
        Assert.Equal("move", eventos[0].GetProperty("type").GetString());
        Assert.Equal(0.5, eventos[0].GetProperty("x").GetDouble(), 3);
        Assert.Equal("text", eventos[3].GetProperty("type").GetString());
        Assert.Equal("oi", eventos[3].GetProperty("text").GetString());

        // Ao soltar, o terminal para de ser controlado.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/devices/{deviceId}/control")).StatusCode);
        var depois = await terminal.GetAsync("/api/v1/device/control").Json();
        Assert.False(depois.GetProperty("controlling").GetBoolean());
    }

    [Fact]
    public async Task Sem_a_tela_aberta_nao_da_para_assumir_o_controle()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, _) = await app.ApprovedDeviceAsync(owner, "PC-02", "Sarah");

        // Sem POST /screen antes, assumir o controle é conflito.
        var r = await owner.PostAsync($"/api/v1/devices/{deviceId}/control", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }
}
