using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Api;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Tests.Api;

public sealed class CrmTests
{
    private const string Key = "sk-ant-chave-de-teste-0123456789";

    /// <summary>IA de mentira: devolve as decisões combinadas, na ordem, e guarda o que recebeu.</summary>
    private sealed class FakeModel : IAssistantModel
    {
        public Queue<object> Decisions { get; } = new();
        public List<AssistantJsonRequest> Requests { get; } = [];

        public Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct) =>
            throw new NotSupportedException("O CRM não usa o chat.");

        public Task<string> AskJsonAsync(AssistantJsonRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(JsonSerializer.Serialize(Decisions.Dequeue()));
        }
    }

    /// <summary>CRM de mentira: aceita o login e guarda o que a Central mandou.</summary>
    private sealed class FakeCrm : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, path, body));
            object answer = path switch
            {
                "/login" => new { success = true, token = "token-do-agente" },
                _ when path.EndsWith("/mensagens", StringComparison.Ordinal) => new { success = true, id = "3EB0" },
                _ when path.EndsWith("/atendimento", StringComparison.Ordinal) => new { success = true, responsavel = new { id = "8", nome = "Kamila Ribeiro" } },
                _ => new { success = false, message = "rota desconhecida" },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") };
        }
    }

    private static object Decide(string acao, string mensagem, string motivo = "teste") => new { acao, mensagem, motivo };

    private static object Aviso(string conversa, string texto, bool fromMe = false, string funil = CrmAgent.TestFunnel, string linha = "556182378969") => new
    {
        evento = "mensagem_recebida",
        conversa = new { id = conversa, contato = new { nome = "Maria Souza", telefone = "5561999990000" }, linha = new { id = "Sarah", nome = "Sarah | 58", numero = linha } },
        mensagem = new { texto, from_me = fromMe },
        funil = new { id = funil, nome = "Funil" },
    };

    private static async Task<(TestApp App, FakeModel Model, FakeCrm Crm, HttpClient Owner, string Webhook)> SetupAsync()
    {
        var model = new FakeModel();
        var crm = new FakeCrm();
        var app = new TestApp
        {
            AssistantModel = model,
            ExtraServices = services => services.AddSingleton(new CrmClient(TimeProvider.System, crm)),
        };
        var owner = await app.OwnerAsync();
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        var saved = await owner.PutAsJsonAsync("/api/v1/neural/crm/settings", new { url = "http://crm.test/", user = "IA_Pontual", password = "segredo", enabled = true }).Json();
        var webhook = new Uri(saved.GetProperty("webhookUrl").GetString()!).AbsolutePath;
        Assert.StartsWith("/api/v1/crm/webhook/", webhook);
        return (app, model, crm, owner, webhook);
    }

    private static async Task WriteAsync(TestApp app, string conversa)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await CrmEndpoints.WriteReplyAsync(scope.ServiceProvider, conversa, CancellationToken.None);
    }

    [Fact]
    public void Le_o_aviso_do_crm_nos_formatos_mais_provaveis()
    {
        var a = CrmAgent.Parse(JsonSerializer.Serialize(Aviso("284841", "Oi, quero portabilidade")));
        Assert.Equal(("284841", "Maria Souza", "5561999990000", CrmAuthor.Client, "Oi, quero portabilidade"), (a.ConversationId, a.Contact, a.Phone, a.Author, a.Text));

        var b = CrmAgent.Parse("""{"data":{"conversation":{"id":77},"message":{"body":"enviado por nós","fromMe":true},"contact":{"name":"João","phone":"556199@c.us"}}}""");
        Assert.Equal(("77", "João", "556199", CrmAuthor.Team, "enviado por nós"), (b.ConversationId, b.Contact, b.Phone, b.Author, b.Text));

        var c = CrmAgent.Parse("""{"conversa_id":"9","mensagem":{"tipo":"audio","transcricao":"quero saber da margem","direcao":"entrada"}}""");
        Assert.Equal(("9", CrmAuthor.Client, "[áudio] quero saber da margem"), (c.ConversationId, c.Author, c.Text));

        Assert.Equal(("556182378969", "Sarah | 58", CrmAgent.TestFunnel), (a.Line, a.LineName, a.Funnel));

        Assert.Equal("", CrmAgent.Parse("não é json").ConversationId);
    }

    [Fact]
    public void Linhas_aceitam_numero_formatado_e_sem_o_nono_digito()
    {
        var lines = CrmAgent.NormalizeLines("55 61 98237-8969, 556182378969; 123");
        Assert.Equal("5561982378969,556182378969", lines);
        Assert.True(CrmAgent.HasLine(lines, "556182378969"));
        Assert.True(CrmAgent.HasLine("5561982378969", "556182378969"));
        Assert.False(CrmAgent.HasLine(lines, "556199990000"));
    }

    [Fact]
    public async Task Fora_do_funil_de_teste_so_atende_cliente_de_quem_faltou()
    {
        var (app, model, _, owner, webhook) = await SetupAsync();
        using var _app = app;
        var anonymous = app.CreateClient();
        var (deviceId, _) = await app.ApprovedDeviceAsync(owner, "PC-09", "Sarah");
        var allDay = Enumerable.Range(0, 7).Select(d => new { day = d, on = true, from = "00:00", to = "23:59" }).ToArray();
        await owner.PutAsJsonAsync($"/api/v1/neural/attendants/{deviceId}/coverage/schedule", new { days = allDay });
        await owner.PutAsJsonAsync($"/api/v1/neural/attendants/{deviceId}/crm-lines", new { lines = "55 61 8237-8969" });

        // Sarah veio trabalhar: a IA não escreve.
        await anonymous.PostAsJsonAsync(webhook, Aviso("500", "Oi", funil: "3"));
        await WriteAsync(app, "500");
        Assert.Empty((await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending").EnumerateArray());

        // Faltou: a resposta fica esperando aprovação, com o nome dela.
        await owner.PutAsJsonAsync($"/api/v1/neural/attendants/{deviceId}/absence", new { absent = true });
        await anonymous.PostAsJsonAsync(webhook, Aviso("500", "Tem alguém aí?", funil: "3"));
        model.Decisions.Enqueue(Decide("responder", "Oi! Estou aqui, como posso ajudar?"));
        await WriteAsync(app, "500");
        var pending = Assert.Single((await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending").EnumerateArray());
        Assert.Equal("Sarah", pending.GetProperty("attendant").GetString());
        Assert.Equal("pending", pending.GetProperty("status").GetString());

        // Cliente de outra linha continua sem resposta.
        await anonymous.PostAsJsonAsync(webhook, Aviso("501", "Oi", funil: "3", linha: "556199990000"));
        await WriteAsync(app, "501");
        Assert.Single((await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task Webhook_recusa_endereco_errado_e_guarda_o_aviso_certo()
    {
        var (app, _, _, owner, webhook) = await SetupAsync();
        using var _app = app;
        var anonymous = app.CreateClient();

        var wrong = await anonymous.PostAsJsonAsync("/api/v1/crm/webhook/errado", Aviso("1", "oi"));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

        var ok = await anonymous.PostAsJsonAsync(webhook + "/mensagem", Aviso("1", "oi"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var overview = await owner.GetAsync("/api/v1/neural/crm").Json();
        var evento = Assert.Single(overview.GetProperty("events").EnumerateArray());
        Assert.Equal("oi", evento.GetProperty("text").GetString());
        Assert.Contains("Maria Souza", evento.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Resposta_so_vai_ao_cliente_depois_de_aprovada()
    {
        var (app, model, crm, owner, webhook) = await SetupAsync();
        using var _app = app;
        var anonymous = app.CreateClient();

        await anonymous.PostAsJsonAsync(webhook, Aviso("284841", "Boa tarde"));
        await anonymous.PostAsJsonAsync(webhook, Aviso("284841", "Quero fazer portabilidade do SIAPE"));
        model.Decisions.Enqueue(Decide("responder", "Boa tarde! Me passa seu CPF, por favor?"));
        await WriteAsync(app, "284841");

        // A IA leu as duas mensagens picadas juntas, e nada foi enviado ainda.
        Assert.Contains("Quero fazer portabilidade do SIAPE", Assert.Single(model.Requests).Input);
        Assert.Contains(model.Requests[0].CachedInstructions!, i => i.Contains("MODO CLIENTES DO CRM"));
        Assert.DoesNotContain(crm.Calls, c => c.Path.EndsWith("/mensagens", StringComparison.Ordinal));

        var pending = Assert.Single((await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending").EnumerateArray());
        Assert.Equal("Boa tarde\nQuero fazer portabilidade do SIAPE", pending.GetProperty("incoming").GetString());
        var id = pending.GetProperty("id").GetString();

        // Mesma mensagem não é respondida duas vezes.
        await WriteAsync(app, "284841");
        Assert.Single(model.Requests);

        var approved = await owner.PostAsJsonAsync($"/api/v1/neural/crm/replies/{id}/approve", new { message = "Boa tarde! Me envia o seu CPF?" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var sent = Assert.Single(crm.Calls, c => c.Path.EndsWith("/mensagens", StringComparison.Ordinal));
        Assert.Equal("/api/agente-externo/v1/conversas/284841/mensagens", sent.Path);
        Assert.Contains("Me envia o seu CPF", JsonDocument.Parse(sent.Body).RootElement.GetProperty("texto").GetString());
        Assert.Equal("sent", (await approved.Json()).GetProperty("status").GetString());

        // Aprovar de novo não manda outra vez.
        var again = await owner.PostAsJsonAsync($"/api/v1/neural/crm/replies/{id}/approve", new { message = "x" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Resposta_velha_nao_sai_quando_o_cliente_escreveu_de_novo()
    {
        var (app, model, crm, owner, webhook) = await SetupAsync();
        using var _app = app;
        var anonymous = app.CreateClient();

        await anonymous.PostAsJsonAsync(webhook, Aviso("5", "Qual o valor que libera?"));
        model.Decisions.Enqueue(Decide("humano", "Vou chamar uma especialista para te ajudar.", "valor depende de consulta"));
        await WriteAsync(app, "5");
        var id = (await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending")[0].GetProperty("id").GetString();

        await anonymous.PostAsJsonAsync(webhook, Aviso("5", "Esquece, já resolvi"));
        var refused = await owner.PostAsJsonAsync($"/api/v1/neural/crm/replies/{id}/approve", new { message = "Vou chamar uma especialista." });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.DoesNotContain(crm.Calls, c => c.Path.EndsWith("/mensagens", StringComparison.Ordinal));

        model.Decisions.Enqueue(Decide("nada", "", "cliente encerrou"));
        await WriteAsync(app, "5");
        var overview = await owner.GetAsync("/api/v1/neural/crm").Json();
        Assert.Empty(overview.GetProperty("pending").EnumerateArray());
        Assert.Contains(overview.GetProperty("recent").EnumerateArray(), r => r.GetProperty("status").GetString() == "outdated");
        Assert.Contains(overview.GetProperty("recent").EnumerateArray(), r => r.GetProperty("status").GetString() == "skipped");
    }

    [Fact]
    public async Task Passar_para_atendente_chama_o_crm()
    {
        var (app, model, crm, owner, webhook) = await SetupAsync();
        using var _app = app;
        await app.CreateClient().PostAsJsonAsync(webhook, Aviso("8", "Quero falar com uma pessoa"));
        model.Decisions.Enqueue(Decide("humano", "Claro, já te passo para uma especialista."));
        await WriteAsync(app, "8");
        var id = (await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending")[0].GetProperty("id").GetString();

        var human = await owner.PostAsJsonAsync($"/api/v1/neural/crm/replies/{id}/human", new { message = "Claro, já te passo para uma especialista." });
        Assert.Equal(HttpStatusCode.OK, human.StatusCode);
        Assert.Contains(crm.Calls, c => c.Method == "PUT" && c.Path == "/api/agente-externo/v1/conversas/8/atendimento" && c.Body.Contains("\"humano\""));
        Assert.Contains("Kamila Ribeiro", (await human.Json()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Desligada_a_ia_nao_escreve()
    {
        var (app, model, _, owner, webhook) = await SetupAsync();
        using var _app = app;
        await owner.PutAsJsonAsync("/api/v1/neural/crm/settings", new { url = "http://crm.test", user = "IA_Pontual", enabled = false });
        await app.CreateClient().PostAsJsonAsync(webhook, Aviso("3", "oi"));
        await WriteAsync(app, "3");
        Assert.Empty(model.Requests);
    }
}
