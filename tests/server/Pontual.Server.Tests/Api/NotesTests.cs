using System.Net;
using System.Net.Http.Json;

namespace Pontual.Server.Tests.Api;

public sealed class NotesTests
{
    private static object Nota(string texto, string cor = "yellow", bool aberta = true) => new
    {
        color = cor,
        content = new object[] { new { t = "p", r = new object[] { new { s = texto, f = 1 } } }, new { t = "li", r = new object[] { new { s = "item", f = 0 } } } },
        x = 100, y = 120, width = 300, height = 280, open = aberta,
    };

    [Fact]
    public async Task Terminal_grava_altera_lista_e_apaga_a_nota()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Bianca");
        var id = Guid.NewGuid();

        var criada = await terminal.PutAsJsonAsync($"/api/v1/device/notes/{id}", Nota("ligar para o cliente"));
        Assert.Equal(HttpStatusCode.OK, criada.StatusCode);

        // Gravar de novo com o mesmo id altera a mesma nota.
        Assert.Equal(HttpStatusCode.OK, (await terminal.PutAsJsonAsync($"/api/v1/device/notes/{id}", Nota("ligar amanhã", "pink", false))).StatusCode);

        var lista = await terminal.GetAsync("/api/v1/device/notes").Json();
        var nota = Assert.Single(lista.GetProperty("notes").EnumerateArray());
        Assert.Equal(id, nota.GetProperty("id").GetGuid());
        Assert.Equal("pink", nota.GetProperty("color").GetString());
        Assert.False(nota.GetProperty("open").GetBoolean());
        Assert.Equal(300, nota.GetProperty("width").GetInt32());
        var blocos = nota.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal("ligar amanhã", blocos[0].GetProperty("r")[0].GetProperty("s").GetString());
        Assert.Equal(1, blocos[0].GetProperty("r")[0].GetProperty("f").GetInt32());
        Assert.Equal("li", blocos[1].GetProperty("t").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await terminal.DeleteAsync($"/api/v1/device/notes/{id}")).StatusCode);
        // Apagar de novo não é erro.
        Assert.Equal(HttpStatusCode.NoContent, (await terminal.DeleteAsync($"/api/v1/device/notes/{id}")).StatusCode);
        Assert.Empty((await terminal.GetAsync("/api/v1/device/notes").Json()).GetProperty("notes").EnumerateArray());
    }

    [Fact]
    public async Task Texto_fora_do_formato_e_cor_desconhecida_nao_passam()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Bianca");

        var html = await terminal.PutAsJsonAsync($"/api/v1/device/notes/{Guid.NewGuid()}", new { color = "yellow", content = "<b>oi</b>" });
        Assert.Equal(HttpStatusCode.BadRequest, html.StatusCode);

        var grande = new { color = "yellow", content = new object[] { new { t = "p", r = new object[] { new { s = new string('a', 20_001), f = 0 } } } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await terminal.PutAsJsonAsync($"/api/v1/device/notes/{Guid.NewGuid()}", grande)).StatusCode);

        // Cor desconhecida vira a amarela; formatação fora da faixa é cortada; caractere de controle some, espaço fica.
        var id = Guid.NewGuid();
        var estranha = new { color = "javascript:alert(1)", content = new object[] { new { t = "x", r = new object[] { new { s = " a\u0001b ", f = 255 } } } } };
        Assert.Equal(HttpStatusCode.OK, (await terminal.PutAsJsonAsync($"/api/v1/device/notes/{id}", estranha)).StatusCode);
        var nota = (await terminal.GetAsync("/api/v1/device/notes").Json()).GetProperty("notes")[0];
        Assert.Equal("yellow", nota.GetProperty("color").GetString());
        var bloco = nota.GetProperty("content")[0];
        Assert.Equal("p", bloco.GetProperty("t").GetString());
        Assert.Equal(" ab ", bloco.GetProperty("r")[0].GetProperty("s").GetString());
        Assert.Equal(15, bloco.GetProperty("r")[0].GetProperty("f").GetInt32());
    }

    [Fact]
    public async Task Uma_maquina_nao_ve_nem_altera_a_nota_de_outra()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, bianca) = await app.ApprovedDeviceAsync(owner, "PC-01", "Bianca");
        var (_, sarah) = await app.ApprovedDeviceAsync(owner, "PC-02", "Sarah");
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await bianca.PutAsJsonAsync($"/api/v1/device/notes/{id}", Nota("da Bianca"))).StatusCode);

        Assert.Empty((await sarah.GetAsync("/api/v1/device/notes").Json()).GetProperty("notes").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await sarah.PutAsJsonAsync($"/api/v1/device/notes/{id}", Nota("da Sarah"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sarah.DeleteAsync($"/api/v1/device/notes/{id}")).StatusCode);

        var nota = Assert.Single((await bianca.GetAsync("/api/v1/device/notes").Json()).GetProperty("notes").EnumerateArray());
        Assert.Equal("da Bianca", nota.GetProperty("content")[0].GetProperty("r")[0].GetProperty("s").GetString());
    }

    [Fact]
    public async Task Maquina_pendente_recebe_recusa_e_nao_lista_vazia()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (_, token) = await app.EnrollAsync(owner, "PC-09");
        var terminal = app.Terminal(token);

        Assert.Equal(HttpStatusCode.Forbidden, (await terminal.GetAsync("/api/v1/device/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminal.PutAsJsonAsync($"/api/v1/device/notes/{Guid.NewGuid()}", Nota("x"))).StatusCode);
    }

    [Fact]
    public async Task Admin_le_as_notas_cola_recado_e_retira_so_o_proprio_recado()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Bianca");
        var dela = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await terminal.PutAsJsonAsync($"/api/v1/device/notes/{dela}", Nota("senha do banco"))).StatusCode);

        var enviado = await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/notes", new { text = "Reunião às 15h\nNa sala 2", color = "blue" });
        Assert.Equal(HttpStatusCode.OK, enviado.StatusCode);
        var recado = (await enviado.Json()).GetProperty("id").GetGuid();

        // O Terminal recebe o recado aberto, com o nome de quem enviou e uma linha por parágrafo.
        var noTerminal = (await terminal.GetAsync("/api/v1/device/notes").Json()).GetProperty("notes").EnumerateArray().Single(n => n.GetProperty("id").GetGuid() == recado);
        Assert.Equal("Paulo", noTerminal.GetProperty("from").GetString());
        Assert.True(noTerminal.GetProperty("open").GetBoolean());
        Assert.Equal("blue", noTerminal.GetProperty("color").GetString());
        Assert.Equal(2, noTerminal.GetProperty("content").GetArrayLength());

        // A Central vê as duas notas.
        Assert.Equal(2, (await owner.GetAsync($"/api/v1/devices/{deviceId}/notes").Json()).GetArrayLength());

        // A nota da atendente não sai pela Central; o recado sai, e o Terminal é avisado.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"/api/v1/devices/{deviceId}/notes/{dela}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/devices/{deviceId}/notes/{recado}")).StatusCode);
        var depois = await terminal.GetAsync("/api/v1/device/notes").Json();
        Assert.Equal(dela, Assert.Single(depois.GetProperty("notes").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(recado, Assert.Single(depois.GetProperty("removed").EnumerateArray()).GetGuid());
        // Um recado retirado não volta se o Terminal tentar gravar por cima.
        Assert.Equal(HttpStatusCode.Conflict, (await terminal.PutAsJsonAsync($"/api/v1/device/notes/{recado}", Nota("de volta"))).StatusCode);

        // Ler, enviar e retirar ficam na auditoria, sem o texto.
        var auditoria = (await owner.GetAsync("/api/v1/audit").Json()).ToString();
        Assert.Contains("abriu as notas adesivas de Bianca", auditoria);
        Assert.Contains("colou um recado na tela de Bianca", auditoria);
        Assert.Contains("retirou um recado da tela de Bianca", auditoria);
        Assert.DoesNotContain("Reunião", auditoria);
    }

    [Fact]
    public async Task Suporte_nao_le_nem_envia_e_recado_vazio_e_recusado()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (deviceId, _) = await app.ApprovedDeviceAsync(owner, "PC-01", "Bianca");
        var suporte = await app.AdminAsync(owner, "Carla", "support");

        Assert.Equal(HttpStatusCode.Forbidden, (await suporte.GetAsync($"/api/v1/devices/{deviceId}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await suporte.PostAsJsonAsync($"/api/v1/devices/{deviceId}/notes", new { text = "oi" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/v1/devices/{deviceId}/notes", new { text = "   " })).StatusCode);
    }
}
