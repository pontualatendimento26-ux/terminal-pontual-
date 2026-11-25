using System.Text.Json;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Tests;

public class LightModeTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("WhatsApp", "https://web.whatsapp.com/")]
    [InlineData("Conversas", "https://web.whatsapp.com/")]
    [InlineData("CRM", "http://10.0.0.9:3000/")]
    [InlineData("PontualConsig", "http://10.0.0.9:3000/")]
    [InlineData("Sistema", "http://192.168.0.243:3000/login")]
    [InlineData("Sistema", "https://crm.empresa.com.br/")]
    public void WhatsApp_e_CRM_nunca_dormem(string name, string target)
    {
        Assert.True(LightMode.StaysConnected(name, target, ["192.168.0.243"]));
        Assert.Equal(TabRest.LowMemory, LightMode.RestFor(true, Now.AddHours(-5), Now));
    }

    [Theory]
    [InlineData("Banco Inter", "https://www.bancointer.com.br/")]
    [InlineData("Nexus", "http://192.168.0.171/")]
    [InlineData("Endereço ruim", "não é endereço")]
    public void Os_outros_sites_podem_dormir(string name, string target)
    {
        Assert.False(LightMode.StaysConnected(name, target, ["192.168.0.243"]));
        Assert.False(LightMode.StaysConnected(name, target, null));
    }

    [Fact]
    public void So_descansa_depois_de_dez_minutos_fora_da_tela()
    {
        Assert.Equal(TabRest.None, LightMode.RestFor(false, null, Now));
        Assert.Equal(TabRest.None, LightMode.RestFor(false, Now.AddMinutes(-9.9), Now));
        Assert.Equal(TabRest.Asleep, LightMode.RestFor(false, Now.AddMinutes(-10), Now));
        Assert.Equal(TabRest.None, LightMode.RestFor(true, Now.AddMinutes(-9.9), Now));
        Assert.Equal(TabRest.LowMemory, LightMode.RestFor(true, Now.AddMinutes(-10), Now));
    }

    [Fact]
    public void Central_antiga_sem_os_campos_novos_grava_como_antes()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var old = JsonSerializer.Deserialize<RecordingPolicy>("""{"record":true,"fps":1,"maxWidth":1920,"quality":50,"segmentSeconds":60}""", web)!;
        Assert.Equal(1.0, old.FrameInterval);
        Assert.Equal(2.0, (old with { FrameSeconds = 2 }).FrameInterval);
        Assert.Equal(5.0, (old with { FrameSeconds = 60 }).FrameInterval);
        Assert.Equal(0.2, (old with { Fps = 5 }).FrameInterval);

        var config = JsonSerializer.Deserialize<TerminalConfig>("""{"version":1,"attendantName":null,"observationMode":false,"items":[]}""", web)!;
        Assert.False(config.LightMode);
        Assert.Null(config.StayConnected);
    }
}
