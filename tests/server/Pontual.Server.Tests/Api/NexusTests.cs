using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Api;
using Pontual.Server.Assistant;

namespace Pontual.Server.Tests.Api;

public sealed class NexusTests
{
    private const string Key = "sk-ant-chave-de-teste-0123456789";
    private const string CpfInss = "22255588846";
    private const string CpfSiape = "11144477735";

    private static string Exemplo(string nome) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Api", "NexusExemplos", nome));

    /// <summary>Nexus de mentira: cada rota devolve o que o teste combinou e guarda o que a Central mandou.</summary>
    private sealed class FakeNexus : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body, string Auth)> Calls { get; } = [];
        public Queue<(int Status, string Body)> Consultas { get; } = new();
        public Queue<(int Status, string Body)> Acompanhamentos { get; } = new();
        public string Localizar { get; set; } = """{"cpf":"x","nome":"CLIENTE EXEMPLO","siape":false,"inss_cache":false,"exercito_cache":false,"na_carteira":false,"na_esteira":false}""";
        public string Saude { get; set; } = """{"ok":true,"setores":{"siape":"ok","inss":"ok","exercito":"fora"},"motivos":{"exercito":"Robô ConsigBR com 31 na fila"}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, path + request.RequestUri.Query, body, request.Headers.Authorization?.ToString() ?? ""));
            var (status, answer) = path switch
            {
                "/api/integracao/v1/saude" => (200, Saude),
                "/api/integracao/v1/localizar" => (200, Localizar),
                "/api/integracao/v1/consultas" => Consultas.Dequeue(),
                _ when path.StartsWith("/api/integracao/v1/consultas/", StringComparison.Ordinal) => Acompanhamentos.Dequeue(),
                _ => (404, """{"codigo":"nao_encontrado","mensagem":"rota"}"""),
            };
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(TestApp App, FakeNexus Nexus, HttpClient Owner, HttpClient Terminal)> SetupAsync(bool configure = true)
    {
        var nexus = new FakeNexus();
        var app = new TestApp { ExtraServices = services => services.AddSingleton(new NexusClient(TimeProvider.System, nexus)) };
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        if (configure)
        {
            var saved = await owner.PutAsJsonAsync("/api/v1/neural/nexus/settings", new { url = "http://nexus.test:3001/api/integracao/v1/", token = "token-da-central" });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.True((await saved.Json()).GetProperty("hasToken").GetBoolean());
        }

        return (app, nexus, owner, terminal);
    }

    [Fact]
    public void Cpf_so_passa_com_digitos_verificadores_certos()
    {
        Assert.Equal("11144477735", NexusClient.NormalizeCpf("111.444.777-35"));
        Assert.Null(NexusClient.NormalizeCpf("111.444.777-36"));
        Assert.Null(NexusClient.NormalizeCpf("1114447773"));
        Assert.Null(NexusClient.NormalizeCpf("111.111.111-11"));
        Assert.Equal("111.444.777-35", NexusClient.FormatCpf("11144477735"));
    }

    [Fact]
    public void Setor_citado_junto_do_cpf_vira_sugestao()
    {
        Assert.Equal("inss", NexusEndpoints.Hint("cpf 22255588846 aposentada do INSS"));
        Assert.Equal("siape", NexusEndpoints.Hint("servidor federal, sougov"));
        Assert.Equal("exercito", NexusEndpoints.Hint("é militar do Exército"));
        Assert.Null(NexusEndpoints.Hint("22255588846"));
    }

    [Fact]
    public void Resumo_repete_o_roteiro_do_nexus_sem_inventar()
    {
        using var siape = JsonDocument.Parse(Exemplo("siape.json"));
        var texto = NexusSummary.Build(siape.RootElement, "siape", fromCentral: false, null);
        Assert.StartsWith("**SIAPE · CLIENTE EXEMPLO · 66 anos**\n**Melhor opção: FACTA — R$", texto);
        Assert.Contains("| FACTA | C1 | R$", texto);
        Assert.Contains("Não passam: BRB, PARANA, DAYCOVAL", texto);
        Assert.Contains("[[detalhes]]", texto);
                Assert.Contains("| Banco | Contrato | Resultado | Libera | Motivo |", texto);
        Assert.Contains("| DIGIO | C1 | 🔎 Conferir | — | Daycoval: conferir saldo devedor mínimo", texto);
        Assert.Contains("| BRB | C1 | ❌ Não passa | — | Daycoval: prazo restante 17x abaixo do mínimo de 30x |", texto);
        Assert.Contains("| INTER | C1 | ⚠️ Atenção |", texto);
        Assert.Contains("- Saldo devedor estimado pelo valor presente", texto);
        Assert.Contains("**C2:** Banco do Brasil · parcela R$", texto);

        using var inss = JsonDocument.Parse(Exemplo("inss.json"));
        texto = NexusSummary.Build(inss.RootElement, "inss", fromCentral: false, null);
        Assert.Contains("**Melhor opção: DIGIO — R$", texto);
        var naoPassam = texto.Split("Não passam: ")[1].Split('\n')[0];
        Assert.Equal(["FINANTO", "FINTECH", "ICRED", "QUALI"], naoPassam.Split(", ").Order().ToArray());
        Assert.Contains("| Banco | Resultado | Libera | Motivo |", texto);
        Assert.Contains("| FINANTO | ❌ Não passa | — | Banco desligado no roteiro do Nexus |", texto);
        Assert.Contains("parcela não cobre o saldo devedor", texto);
        Assert.Contains("espécie 87", texto);
        Assert.Contains("do Nexus de 11/11", texto);
        Assert.DoesNotContain("Resultado reaproveitado", texto);
    }

    [Fact]
    public async Task Sem_token_o_cpf_segue_para_a_ia_como_antes()
    {
        var (app, nexus, _, terminal) = await SetupAsync(configure: false);
        using var _app = app;
        var r = await terminal.GetAsync($"/api/v1/device/nexus/localizar?cpf={CpfInss}").Json();
        Assert.False(r.GetProperty("configured").GetBoolean());
        Assert.Empty(nexus.Calls);
    }

    [Fact]
    public async Task Localizar_sugere_o_setor_e_avisa_o_que_custa()
    {
        var (app, nexus, _, terminal) = await SetupAsync();
        using var _app = app;
        nexus.Localizar = """{"cpf":"11144477735","nome":"CLIENTE EXEMPLO","siape":true,"inss_cache":true,"exercito_cache":false,"na_carteira":true,"na_esteira":false}""";

        var r = await terminal.GetAsync($"/api/v1/device/nexus/localizar?cpf={CpfSiape}").Json();
        Assert.Equal("siape", r.GetProperty("sugestao").GetString());
        Assert.Equal("111.444.777-35", r.GetProperty("cpf").GetString());
        var setores = r.GetProperty("setores").EnumerateArray().ToDictionary(s => s.GetProperty("setor").GetString()!);
        Assert.Equal("gratis", setores["siape"].GetProperty("custo").GetString());
        Assert.Equal("guardado", setores["inss"].GetProperty("custo").GetString());
        Assert.Equal("fora", setores["exercito"].GetProperty("estado").GetString());
        Assert.Contains("31 na fila", setores["exercito"].GetProperty("motivo").GetString());
        Assert.All(nexus.Calls, c => Assert.Equal("Bearer token-da-central", c.Auth));
        Assert.Contains(nexus.Calls, c => c.Path == $"/api/integracao/v1/localizar?cpf={CpfSiape}");

        // O que a atendente escreveu junto do CPF vale mais que o resto.
        var dica = await terminal.GetAsync($"/api/v1/device/nexus/localizar?cpf={CpfSiape}&dica=aposentado%20INSS").Json();
        Assert.Equal("inss", dica.GetProperty("sugestao").GetString());

        var invalido = await terminal.GetAsync("/api/v1/device/nexus/localizar?cpf=11144477736");
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
    }

    [Fact]
    public async Task Consulta_paga_so_com_confirmacao_e_o_resultado_fica_24h_guardado()
    {
        var (app, nexus, owner, terminal) = await SetupAsync();
        using var _app = app;

        // Sem confirmar: o Nexus diz que cobraria; nada é gravado na conversa.
        nexus.Consultas.Enqueue((402, """{"codigo":"consulta_paga_nao_autorizada","mensagem":"Esta consulta seria cobrada."}"""));
        var semConfirmar = await terminal.PostAsJsonAsync("/api/v1/device/nexus/consultas", new { cpf = CpfInss, setor = "inss", allowPaid = false }).Json();
        Assert.Equal("precisa_autorizar", semConfirmar.GetProperty("status").GetString());
        var conversa = await terminal.GetAsync("/api/v1/device/assistant").Json();
        Assert.Empty(conversa.GetProperty("messages").EnumerateArray());

        // Confirmada: o Nexus responde 202, a Central acompanha e o resultado vira uma fala da assistente.
        nexus.Consultas.Enqueue((202, """{"consulta_id":"7b9d4f20-1c3e-4a85-8d17-5e6f7a8b9c62","status":"em_andamento","previsao_segundos":30}"""));
        var pedido = await terminal.PostAsJsonAsync("/api/v1/device/nexus/consultas", new { cpf = "222.555.888-46", setor = "inss", allowPaid = true }).Json();
        Assert.Equal("em_andamento", pedido.GetProperty("status").GetString());
        Assert.Equal(30, pedido.GetProperty("previsaoSegundos").GetInt32());
        var enviado = JsonDocument.Parse(nexus.Calls.Last(c => c.Path == "/api/integracao/v1/consultas").Body).RootElement;
        Assert.True(enviado.GetProperty("permitir_consulta_paga").GetBoolean());
        Assert.Equal("Maria Souza", enviado.GetProperty("solicitante").GetProperty("nome").GetString());
        Assert.Equal(CpfInss, enviado.GetProperty("cpf").GetString());

        var id = pedido.GetProperty("id").GetString();
        nexus.Acompanhamentos.Enqueue((200, """{"consulta_id":"7b9d4f20-1c3e-4a85-8d17-5e6f7a8b9c62","setor":"inss","status":"em_andamento","previsao_segundos":20}"""));
        var andando = await terminal.GetAsync($"/api/v1/device/nexus/consultas/{id}").Json();
        Assert.Equal("em_andamento", andando.GetProperty("status").GetString());

        nexus.Acompanhamentos.Enqueue((200, Exemplo("inss.json")));
        var pronto = await terminal.GetAsync($"/api/v1/device/nexus/consultas/{id}").Json();
        Assert.Equal("concluida", pronto.GetProperty("status").GetString());
        Assert.Contains("**Melhor opção: DIGIO", pronto.GetProperty("resposta").GetProperty("text").GetString());
        Assert.EndsWith("/consultas/7b9d4f20-1c3e-4a85-8d17-5e6f7a8b9c62", nexus.Calls.Last().Path);

        var falas = (await terminal.GetAsync("/api/v1/device/assistant").Json()).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, falas.Count);
        Assert.Equal("Consultar no Nexus: CPF 222.555.888-46 — INSS", falas[0].GetProperty("text").GetString());
        Assert.StartsWith("**INSS · CLIENTE EXEMPLO", falas[1].GetProperty("text").GetString());

        // De novo, dentro de 24 horas: sai da Central, sem chamar o Nexus.
        var chamadas = nexus.Calls.Count;
        var deNovo = await terminal.PostAsJsonAsync("/api/v1/device/nexus/consultas", new { cpf = CpfInss, setor = "inss", allowPaid = false }).Json();
        Assert.Equal("concluida", deNovo.GetProperty("status").GetString());
        Assert.Contains("guardado de", deNovo.GetProperty("resposta").GetProperty("text").GetString());
        Assert.Equal(chamadas, nexus.Calls.Count);

        var localizar = await terminal.GetAsync($"/api/v1/device/nexus/localizar?cpf={CpfInss}").Json();
        Assert.Equal("inss", localizar.GetProperty("sugestao").GetString());

        var painel = await owner.GetAsync("/api/v1/neural/nexus").Json();
        Assert.Equal(2, painel.GetProperty("recent").GetArrayLength());
        // O exemplo de INSS do Nexus já veio do cache dele; a segunda saiu do guardado da Central: nenhuma custou.
        Assert.Equal(2, painel.GetProperty("last24h").GetProperty("guardadas").GetInt32());
        Assert.Equal(0, painel.GetProperty("last24h").GetProperty("pagas").GetInt32());
    }

    [Fact]
    public async Task Erros_do_nexus_viram_frases_para_a_atendente()
    {
        var (app, nexus, _, terminal) = await SetupAsync();
        using var _app = app;

        nexus.Consultas.Enqueue((403, """{"codigo":"ip_nao_autorizado","mensagem":"IP 10.0.0.5 não autorizado para a integração."}"""));
        var ip = await terminal.PostAsJsonAsync("/api/v1/device/nexus/consultas", new { cpf = CpfSiape, setor = "siape" }).Json();
        Assert.Equal("erro", ip.GetProperty("status").GetString());
        Assert.Contains("Avise o TI", ip.GetProperty("mensagem").GetString());

        nexus.Consultas.Enqueue((404, """{"consulta_id":"7b9d4f20-1c3e-4a85-8d17-5e6f7a8b9c62","codigo":"cpf_nao_encontrado","mensagem":"CPF não encontrado no SIAPE","fonte":{"origem":"nova","paga":false}}"""));
        var naoAchou = await terminal.PostAsJsonAsync("/api/v1/device/nexus/consultas", new { cpf = CpfSiape, setor = "siape" }).Json();
        Assert.Equal("nao_encontrado", naoAchou.GetProperty("status").GetString());
        Assert.Contains("CPF não encontrado no SIAPE", naoAchou.GetProperty("resposta").GetProperty("text").GetString());

        var setorErrado = await terminal.PostAsJsonAsync("/api/v1/device/nexus/consultas", new { cpf = CpfSiape, setor = "clt" });
        Assert.Equal(HttpStatusCode.BadRequest, setorErrado.StatusCode);

        // A falha não deixa a pergunta solta na conversa; o "não encontrado" fica (pergunta e resposta).
        var falas = (await terminal.GetAsync("/api/v1/device/assistant").Json()).GetProperty("messages").GetArrayLength();
        Assert.Equal(2, falas);
    }

    [Fact]
    public async Task So_admin_ve_e_muda_a_ligacao_com_o_nexus()
    {
        var (app, _, owner, terminal) = await SetupAsync();
        using var _app = app;
        var overview = await owner.GetAsync("/api/v1/neural/nexus").Json();
        Assert.Equal("http://nexus.test:3001/api/integracao/v1", overview.GetProperty("url").GetString());
        Assert.DoesNotContain("token-da-central", overview.GetRawText());

        var anonimo = await app.CreateClient().GetAsync("/api/v1/neural/nexus");
        Assert.NotEqual(HttpStatusCode.OK, anonimo.StatusCode);
        var peloTerminal = await terminal.GetAsync("/api/v1/neural/nexus");
        Assert.NotEqual(HttpStatusCode.OK, peloTerminal.StatusCode);
    }
}
