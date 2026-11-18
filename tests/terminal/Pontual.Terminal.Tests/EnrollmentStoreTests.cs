using System.Security.Cryptography;
using System.Text;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Tests;

public sealed class EnrollmentStoreTests : IDisposable
{
    private const string Token = "tok_3f9c1a7e-segredo-ç";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pontual-testes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private EnrollmentStore NewStore(ISecretProtector? protector = null) => new(_directory, protector ?? new DpapiProtector());

    [Fact]
    public void Sem_arquivo_nao_ha_cadastro()
    {
        Assert.Null(NewStore().Load());
    }

    [Fact]
    public void Cadastro_de_servidor_e_gravado_e_lido_com_dpapi()
    {
        var store = NewStore();
        store.Save(Enrollment.ForServer("http://192.168.0.10:5080", "7d0a9a34-0000-4000-8000-000000000001", Token));

        var read = NewStore().Load();

        Assert.NotNull(read);
        Assert.Equal(TerminalMode.Server, read.Mode);
        Assert.Equal("http://192.168.0.10:5080", read.ServerUrl);
        Assert.Equal("7d0a9a34-0000-4000-8000-000000000001", read.DeviceId);
        Assert.Equal(Token, read.Token);
    }

    [Fact]
    public void Token_nao_fica_em_texto_claro_no_arquivo()
    {
        var store = NewStore();
        store.Save(Enrollment.ForServer("http://192.168.0.10:5080", "dev-1", Token));

        var bytes = File.ReadAllBytes(store.FilePath);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Token)), text);
        Assert.Contains("\"mode\": \"server\"", text);
        Assert.Contains("\"serverUrl\": \"http://192.168.0.10:5080\"", text);
        Assert.Contains("\"deviceId\": \"dev-1\"", text);
        Assert.Contains("\"protectedToken\"", text);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Modo_local_e_gravado_sem_dados_de_servidor()
    {
        var store = NewStore();
        store.Save(Enrollment.Local);

        Assert.Equal(Enrollment.Local, NewStore().Load());
        Assert.DoesNotContain("protectedToken", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Salvar_de_novo_substitui_o_cadastro()
    {
        var store = NewStore();
        store.Save(Enrollment.ForServer("http://a:5080", "dev-1", "um"));
        store.Save(Enrollment.Local);
        store.Save(Enrollment.ForServer("http://b:5080", "dev-2", "dois"));

        Assert.Equal(Enrollment.ForServer("http://b:5080", "dev-2", "dois"), store.Load());
    }

    [Fact]
    public void Apagar_remove_o_cadastro()
    {
        var store = NewStore();
        store.Save(Enrollment.ForServer("http://a:5080", "dev-1", Token));

        store.Delete();
        store.Delete();

        Assert.Null(store.Load());
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("isto não é json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "mode": "outro" }""")]
    [InlineData("""{ "mode": "server", "serverUrl": "http://a:5080", "deviceId": "d" }""")]
    [InlineData("""{ "mode": "server", "serverUrl": "http://a:5080", "deviceId": "d", "protectedToken": "não é base64!" }""")]
    [InlineData("""{ "mode": "server", "serverUrl": "http://a:5080", "deviceId": "d", "protectedToken": "AAAA" }""")]
    [InlineData("""{ "mode": "server", "serverUrl": "ftp://a", "deviceId": "d", "protectedToken": "AAAA" }""")]
    public void Arquivo_corrompido_vale_como_sem_cadastro(string content)
    {
        Directory.CreateDirectory(_directory);
        var store = NewStore();
        File.WriteAllText(store.FilePath, content);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Token_adulterado_vale_como_sem_cadastro()
    {
        var store = NewStore();
        var json = store.Serialize(Enrollment.ForServer("http://a:5080", "dev-1", Token));
        var marker = "\"protectedToken\": \"";
        var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var tampered = json[..(start + 40)] + (json[start + 40] == 'A' ? 'B' : 'A') + json[(start + 41)..];

        Assert.NotNull(store.Parse(json));
        Assert.Null(store.Parse(tampered));
    }

    [Fact]
    public void Montagem_e_leitura_usam_o_protetor_recebido()
    {
        var store = NewStore(new ReverseProtector());

        var json = store.Serialize(Enrollment.ForServer("https://pontual.exemplo", "dev-9", "abc"));

        Assert.Contains(Convert.ToBase64String("cba"u8), json);
        Assert.Equal(Enrollment.ForServer("https://pontual.exemplo", "dev-9", "abc"), store.Parse(json));
    }

    [Fact]
    public void Dpapi_devolve_o_mesmo_valor_e_recusa_lixo()
    {
        var protector = new DpapiProtector();
        var plain = Encoding.UTF8.GetBytes(Token);

        var cipher = protector.Protect(plain);

        Assert.NotEqual(plain, cipher);
        Assert.Equal(plain, protector.Unprotect(cipher));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect([1, 2, 3, 4]));
    }

    [Theory]
    [InlineData("http://192.168.0.10:5080", "http://192.168.0.10:5080")]
    [InlineData("  http://192.168.0.10:5080/  ", "http://192.168.0.10:5080")]
    [InlineData("192.168.0.10:5080", "http://192.168.0.10:5080")]
    [InlineData("servidor-ti:5080", "http://servidor-ti:5080")]
    [InlineData("HTTP://Servidor:5080/central/", "http://servidor:5080")]
    [InlineData("https://pontual.exemplo.com.br/terminal/?x=1", "https://pontual.exemplo.com.br")]
    public void Endereco_do_servidor_e_normalizado(string input, string expected)
    {
        Assert.True(EnrollmentStore.TryNormalizeServerUrl(input, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ftp://servidor")]
    [InlineData("file:///C:/x")]
    [InlineData("http://usuario:senha@servidor:5080")]
    [InlineData("http://")]
    [InlineData("isto não é endereço")]
    public void Endereco_invalido_e_recusado(string? input)
    {
        Assert.False(EnrollmentStore.TryNormalizeServerUrl(input, out _));
    }

    private sealed class ReverseProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Reverse().ToArray();

        public byte[] Unprotect(byte[] cipher) => cipher.Reverse().ToArray();
    }
}
