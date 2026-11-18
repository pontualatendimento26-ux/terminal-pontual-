using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Tests;

public class HostPolicyTests
{
    private static readonly HostPolicy Policy = new(["safra.com.br", "web.whatsapp.com", "192.168.0.243", "localhost", "Front.ParanaBanco.B.BR"]);

    [Theory]
    [InlineData("https://safra.com.br/")]
    [InlineData("https://safra.com.br/Home/Login?x=1#topo")]
    [InlineData("http://safra.com.br/")]
    [InlineData("https://web.whatsapp.com/")]
    public void Dominio_igual_ao_da_lista_e_permitido(string url)
    {
        Assert.Equal(NavigationVerdict.Allowed, Policy.Evaluate(url).Verdict);
    }

    [Theory]
    [InlineData("https://epfweb.safra.com.br/Home/Login", "epfweb.safra.com.br")]
    [InlineData("https://a.b.c.safra.com.br/", "a.b.c.safra.com.br")]
    public void Subdominio_e_permitido(string url, string host)
    {
        Assert.Equal(new NavigationDecision(NavigationVerdict.Allowed, host), Policy.Evaluate(url));
    }

    [Theory]
    [InlineData("https://safra.com.br.evil.com/", "safra.com.br.evil.com")]
    [InlineData("https://xsafra.com.br/", "xsafra.com.br")]
    [InlineData("https://evil.com/safra.com.br", "evil.com")]
    [InlineData("https://evil.com/?u=https://safra.com.br/", "evil.com")]
    [InlineData("https://safra.com.br@evil.com/", "evil.com")]
    [InlineData("https://safra.com.br:senha@evil.com/", "evil.com")]
    [InlineData("https://com.br/", "com.br")]
    [InlineData("https://whatsapp.com/", "whatsapp.com")]
    [InlineData("https://safra.com/", "safra.com")]
    public void Dominio_parecido_nao_passa(string url, string host)
    {
        Assert.Equal(new NavigationDecision(NavigationVerdict.NotListed, host), Policy.Evaluate(url));
    }

    [Theory]
    [InlineData("HTTPS://EPFWEB.SAFRA.COM.BR/Home")]
    [InlineData("https://Safra.Com.Br/")]
    [InlineData("https://front.paranabanco.b.br/login")]
    public void Maiusculas_nao_mudam_o_resultado(string url)
    {
        Assert.Equal(NavigationVerdict.Allowed, Policy.Evaluate(url).Verdict);
    }

    [Theory]
    [InlineData("https://safra.com.br:8443/")]
    [InlineData("http://192.168.0.243:3000/html/login.html")]
    [InlineData("http://localhost:5080/terminal/")]
    public void Porta_nao_muda_o_resultado(string url)
    {
        Assert.Equal(NavigationVerdict.Allowed, Policy.Evaluate(url).Verdict);
    }

    [Fact]
    public void Entrada_da_lista_com_porta_esquema_ou_caminho_vale_pelo_dominio()
    {
        var policy = new HostPolicy(["192.168.0.243:3000", "https://portal.exemplo.com.br/login", " akipromotora.app. "]);

        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("http://192.168.0.243:3000/").Verdict);
        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("https://portal.exemplo.com.br/").Verdict);
        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("https://akipromotora.app/x").Verdict);
        Assert.Equal(3, policy.Hosts.Count);
    }

    [Theory]
    [InlineData("http://192.168.0.244/")]
    [InlineData("http://1.192.168.0.243/")]
    [InlineData("http://192.168.0.243.evil.com/")]
    [InlineData("http://10.192.168.0.243/")]
    public void Ip_so_vale_por_igualdade(string url)
    {
        Assert.Equal(NavigationVerdict.NotListed, Policy.Evaluate(url).Verdict);
    }

    [Fact]
    public void Nome_de_um_rotulo_so_nao_libera_subdominios()
    {
        Assert.Equal(NavigationVerdict.Allowed, Policy.Evaluate("http://localhost/").Verdict);
        Assert.Equal(NavigationVerdict.NotListed, Policy.Evaluate("http://evil.localhost/").Verdict);
    }

    [Fact]
    public void Ponto_final_no_dominio_nao_muda_o_resultado()
    {
        Assert.Equal(NavigationVerdict.Allowed, Policy.Evaluate("https://epfweb.safra.com.br./").Verdict);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>oi</h1>")]
    [InlineData("ftp://safra.com.br/")]
    [InlineData("mailto:alguem@safra.com.br")]
    [InlineData("ms-settings:network")]
    [InlineData("edge://settings")]
    [InlineData("about:config")]
    [InlineData("view-source:https://safra.com.br/")]
    [InlineData("blob:https://safra.com.br/1234")]
    [InlineData("//safra.com.br/")]
    [InlineData("safra.com.br")]
    [InlineData("")]
    [InlineData(null)]
    public void Esquema_invalido_e_sempre_bloqueado(string? url)
    {
        Assert.Equal(NavigationVerdict.SchemeBlocked, Policy.Evaluate(url).Verdict);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("ABOUT:BLANK")]
    [InlineData("about:blank#topo")]
    public void About_blank_e_a_unica_excecao_de_esquema(string url)
    {
        Assert.Equal(NavigationVerdict.Allowed, Policy.Evaluate(url).Verdict);
    }

    [Fact]
    public void Lista_vazia_nao_permite_nenhum_site()
    {
        Assert.Equal(NavigationVerdict.NotListed, HostPolicy.Empty.Evaluate("https://safra.com.br/").Verdict);
        Assert.Equal(NavigationVerdict.NotListed, new HostPolicy(["", "   ", "http://", "::"]).Evaluate("https://safra.com.br/").Verdict);
    }

    [Fact]
    public void Politica_junta_os_dominios_de_todos_os_itens_da_configuracao()
    {
        var config = new TerminalConfig(3, "Marina", true,
        [
            new ConfigItem("1", "Safra", "site", "https://epfweb.safra.com.br/Home/Login", "Bancos e promotoras", ["epfweb.safra.com.br"]),
            new ConfigItem("2", "Gmail", "site", "https://mail.google.com/", "Comunicação", ["mail.google.com", "accounts.google.com"]),
            new ConfigItem("3", "Excel", "program", @"C:\Office\EXCEL.EXE", "Pontual e ferramentas", []),
        ]);

        var policy = HostPolicy.FromConfig(config);

        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("https://accounts.google.com/signin").Verdict);
        Assert.Equal(NavigationVerdict.Allowed, policy.Evaluate("https://epfweb.safra.com.br/").Verdict);
        Assert.Equal(NavigationVerdict.NotListed, policy.Evaluate("https://www.safra.com.br/").Verdict);
        Assert.Equal(NavigationVerdict.NotListed, policy.Evaluate("https://google.com/").Verdict);
    }
}
