using System.Text.Json;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Tests;

public class ChromeWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 13, 0, 0, TimeSpan.Zero);
    private static readonly HostPolicy Operational = new(["web.whatsapp.com", "safra.com.br"]);

    [Theory]
    [InlineData("web.whatsapp.com/", "web.whatsapp.com")]
    [InlineData("https://www.YouTube.com/watch?v=1", "www.youtube.com")]
    [InlineData("192.168.0.243:3000/html/login.html", "192.168.0.243")]
    [InlineData("exemplo.com.br:8080/a", "exemplo.com.br")]
    public void Endereco_da_barra_vira_dominio(string address, string host)
    {
        Assert.Equal(host, ChromeWatch.HostFromAddress(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chrome://settings")]
    [InlineData("chrome://newtab/")]
    [InlineData("about:blank")]
    [InlineData("mailto:x@y.com")]
    [InlineData("receita de bolo")]
    [InlineData("bolo")]
    [InlineData("file:///C:/x.pdf")]
    public void O_que_nao_e_site_e_ignorado(string? address)
    {
        Assert.Null(ChromeWatch.HostFromAddress(address));
    }

    [Fact]
    public void Avisa_uma_vez_e_depois_um_por_minuto_enquanto_fica_na_tela()
    {
        var watch = new ChromeWatch();
        Assert.Equal([(DeviceEvent.ChromeVisit, "www.youtube.com")],
            watch.Update(["web.whatsapp.com", "epfweb.safra.com.br", "www.youtube.com", "www.youtube.com"], Operational.IsHostAllowed, T0));

        // Leitura a cada 3 segundos, como no Terminal: em pouco mais de 2 minutos, dois "continua aberto".
        var events = new List<(string, string)>();
        for (var s = 3; s <= 125; s += 3)
        {
            events.AddRange(watch.Update(["www.youtube.com"], Operational.IsHostAllowed, T0.AddSeconds(s)));
        }

        Assert.Equal([(DeviceEvent.ChromeStay, "www.youtube.com"), (DeviceEvent.ChromeStay, "www.youtube.com")], events);
    }

    [Fact]
    public void Sumido_por_alguns_minutos_conta_como_visita_nova()
    {
        var watch = new ChromeWatch();
        watch.Update(["jogo.com"], Operational.IsHostAllowed, T0);
        Assert.Empty(watch.Update([], Operational.IsHostAllowed, T0.AddMinutes(2)));
        Assert.Empty(watch.Update(["jogo.com"], Operational.IsHostAllowed, T0.AddMinutes(2.5)));
        watch.Update([], Operational.IsHostAllowed, T0.AddMinutes(6));
        Assert.Equal([(DeviceEvent.ChromeVisit, "jogo.com")], watch.Update(["jogo.com"], Operational.IsHostAllowed, T0.AddMinutes(6.1)));
    }

    [Fact]
    public void Configuracao_traz_as_listas_do_Chrome_e_servidor_antigo_nao_quebra()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var novo = JsonSerializer.Deserialize<TerminalConfig>(
            """{"version":4,"items":[],"chromeAllowed":["Accounts.Google.com"," "],"chromeBlocked":["https://www.youtube.com/x"]}""", options)!;
        var limpo = ServerApi.Sanitize(novo);
        Assert.Equal(["accounts.google.com"], limpo.ChromeAllowed);
        Assert.Equal(["www.youtube.com"], limpo.ChromeBlocked);

        var antigo = ServerApi.Sanitize(JsonSerializer.Deserialize<TerminalConfig>("""{"version":4,"items":[]}""", options)!);
        Assert.Empty(antigo.ChromeAllowed!);
        Assert.Empty(antigo.ChromeBlocked!);
    }
}
