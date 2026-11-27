using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Assistant;

namespace Pontual.Server.Tests.Api;

/// <summary>Valor liberado do SIAPE: a base é uma lista de mentira; a IA e o CRM também.</summary>
public sealed class SiapeTests
{
    private const string Key = "sk-ant-chave-de-teste-0123456789";

    private sealed class FakeModel : IAssistantModel
    {
        public Queue<object> Decisions { get; } = new();
        public List<AssistantJsonRequest> Requests { get; } = [];

        public Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct) =>
            throw new NotSupportedException("O valor liberado não usa o chat.");

        public Task<string> AskJsonAsync(AssistantJsonRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(JsonSerializer.Serialize(Decisions.Dequeue()));
        }
    }

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
                "/api/sessions" => new object[] { new { id = "Kamila_4", canal_id = 4 }, new { id = "Siape_6", canal_id = 6 } },
                "/api/agente-externo/v1/etapas" => new object[] { new { id = 1 } },
                "/api/tags" when request.Method == HttpMethod.Get => new object[] { new { id = 7, name = "Atendida pela IA" } },
                "/api/chat/send" or "/api/chat/tags/toggle" => new { success = true },
                "/api/chat/messages" => new { success = true, messages = Array.Empty<object>() },
                _ => new { success = false, message = "rota desconhecida" },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeSiape : ISiapeSource
    {
        public List<SiapeTable> Tables { get; } =
        [
            new("COMPLETA_NOVEMBRO_CORRIGIDA", new DateTime(2026, 11, 25), ["banco", "nome", "cpf", "Margem", "TELEFONE1", "NOME_ORGAO", "IDADE", "uf", "TOTAL_LIBERADO", "POSITIVACAO", "VALOR_LIBERADO", "REEMBOLSO"]),
            new("COMPLETA_OUTUBRO_REFEITA", new DateTime(2026, 11, 11), ["banco", "nome", "cpf", "Margem", "NOME_ORGAO", "IDADE", "TOTAL_LIBERADO", "POSITIVACAO"]),
            new("MARGENS_NOVEMBRO", new DateTime(2026, 11, 25), ["Matricula", "CPF", "Margem"]),
            new("TAXAS 2026", new DateTime(2026, 11, 25), ["BANCO1", "PRAZO", "TAXA"]),
        ];

        public List<SiapeCandidate> Candidates { get; } = [];
        public List<(string Current, decimal Min, int Take)> Asked { get; } = [];

        public Task<(string Server, IReadOnlyList<SiapeTable> Tables)> ProbeAsync(SiapeAccess access, CancellationToken ct) =>
            Task.FromResult<(string, IReadOnlyList<SiapeTable>)>(("KAMILA\\SQLEXPRESS", Tables));

        public Task<IReadOnlyList<SiapeCandidate>> CandidatesAsync(SiapeAccess access, SiapeTable current, decimal minLiberado, int take, CancellationToken ct)
        {
            Asked.Add((current.Name, minLiberado, take));
            return Task.FromResult<IReadOnlyList<SiapeCandidate>>(Candidates.Where(c => c.Liberado >= minLiberado).OrderByDescending(c => c.Liberado).Take(take).ToList());
        }
    }

    private static object Decide(string acao, string mensagem, string motivo = "teste") => new { acao, mensagem, motivo };

    private static async Task<(TestApp App, FakeModel Model, FakeCrm Crm, FakeSiape Siape, HttpClient Owner)> SetupAsync(bool autoSend = false)
    {
        var model = new FakeModel();
        var crm = new FakeCrm();
        var siape = new FakeSiape();
        var app = new TestApp
        {
            AssistantModel = model,
            ExtraServices = services =>
            {
                services.AddSingleton(new CrmClient(TimeProvider.System, crm));
                services.AddSingleton<ISiapeSource>(siape);
            },
        };
        var owner = await app.OwnerAsync();
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        await owner.PutAsJsonAsync("/api/v1/neural/crm/settings", new { url = "http://crm.test/", user = "IA_Pontual", password = "segredo", enabled = true });
        var saved = await owner.PutAsJsonAsync("/api/v1/neural/siape/settings", new
        {
            server = "192.168.0.160,1433", database = "SIAPE", user = "pontual_acesso", password = "segredo-sql",
            minLiberado = 1000, max = 3, day = 1, botId = "Siape_6", attendant = "Kamila", enabled = true, autoSend,
        }).Json();
        Assert.True(saved.GetProperty("configured").GetBoolean());
        Assert.True(saved.GetProperty("hasPassword").GetBoolean());
        Assert.StartsWith("Base do mês: COMPLETA_NOVEMBRO_CORRIGIDA", saved.GetProperty("chosen").GetString());
        Assert.Contains("positivação", saved.GetProperty("chosen").GetString());
        return (app, model, crm, siape, owner);
    }

    [Fact]
    public void Le_o_mes_pelo_nome_da_tabela_e_escolhe_a_base_do_mes()
    {
        Assert.Equal(2026 * 12 + 10, SiapeMargin.MonthOf("SERV 2026 11 MARGEM 1", new DateTime(2026, 11, 25)));
        Assert.Equal(2026 * 12 + 10, SiapeMargin.MonthOf("COMPLETA_NOVEMBRO_CORRIGIDA", new DateTime(2026, 11, 25)));
        Assert.Equal(2026 * 12 + 9, SiapeMargin.MonthOf("COMPLETA_OUTUBRO_REFEITA", new DateTime(2026, 11, 11)));
        Assert.Equal(2025 * 12 + 11, SiapeMargin.MonthOf("BASE DEZEMBRO", new DateTime(2026, 1, 5)));
        Assert.Null(SiapeMargin.MonthOf("lemit concra atual", new DateTime(2026, 11, 25)));

        var tables = new FakeSiape().Tables;
        var (current, _) = SiapeMargin.Choose(tables, "");
        Assert.Equal("COMPLETA_NOVEMBRO_CORRIGIDA", current!.Name);

        // A escolha da tela vale; a de outubro não tem telefone e é recusada.
        var (c2, why2) = SiapeMargin.Choose(tables, "COMPLETA_OUTUBRO_REFEITA");
        Assert.Null(c2);
        Assert.Contains("TELEFONE1", why2);
        var (c3, why) = SiapeMargin.Choose(tables, "SUMIU");
        Assert.Null(c3);
        Assert.Contains("não existe mais", why);
    }

    [Fact]
    public void Monta_o_chat_id_so_para_celular()
    {
        Assert.Equal("5524999999967@c.us", SiapeMargin.ChatIdOf("24999999967"));
        Assert.Equal("5561982378969@c.us", SiapeMargin.ChatIdOf("5561982378969"));
        Assert.Equal("", SiapeMargin.ChatIdOf("2133334444")); // fixo
        Assert.Equal("", SiapeMargin.ChatIdOf(""));
        Assert.Equal("Maria da Silva", SiapeMargin.Pretty("MARIA DA SILVA"));
    }

    [Fact]
    public async Task Busca_quem_tem_valor_liberado_escreve_como_a_atendente_e_espera_aprovacao()
    {
        var (app, model, crm, siape, owner) = await SetupAsync();
        using var _app = app;
        siape.Candidates.AddRange(
        [
            new("65500000001", "LUCINEA FERREIRA", "24999999967", 18500m, 900m, "MINISTERIO DA SAUDE", 52, "RJ", 1234.56m, [new("BANCO DO BRASIL", 12000m), new("FINANCEIRA BRB", 6500m)]),
            new("65500000002", "JOAO FIXO", "2133334444", 15000m, 200m, "INSS", 60, "SP"), // telefone fixo: fora
            new("65500000003", "ANA NOVA", "61982378969", 2300m, 0m, "", 0, "DF"),
            new("65500000004", "POUCO", "61982378900", 800m, 0m, "", 0, "DF"), // abaixo de R$ 1.000: fora
        ]);
        model.Decisions.Enqueue(Decide("responder", "Oii Lucinea, aqui é da Pontual 😊 Liberou margem nova no seu contracheque este mês. Quer que eu faça uma simulação sem compromisso?"));
        model.Decisions.Enqueue(Decide("responder", "Oii Ana, aqui é da Pontual! Apareceu margem nova para você. Posso simular?"));

        var scan = await (await owner.PostAsync("/api/v1/neural/siape/scan", null)).Json();
        Assert.Equal(2, scan.GetProperty("created").GetInt32());
        Assert.Equal(0, scan.GetProperty("sent").GetInt32());
        Assert.Contains("esperando aprovação", scan.GetProperty("message").GetString());

        // A base lida foi a do mês, com o mínimo da tela.
        var asked = Assert.Single(siape.Asked);
        Assert.Equal(("COMPLETA_NOVEMBRO_CORRIGIDA", 1000m), (asked.Current, asked.Min));

        // A IA viu os dados do servidor, a atendente e o modo valor liberado.
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("<atendente>Kamila</atendente>", model.Requests[0].Input);
        Assert.Contains("Nome: Lucinea Ferreira", model.Requests[0].Input);
        var input = model.Requests[0].Input.Replace(' ', ' ');
        Assert.Contains("Total liberado para consignado neste mês: R$ 18.500,00", input);
        Assert.Contains("Reembolso: R$ 1.234,56", input);
        Assert.Contains("- Banco do Brasil: R$ 12.000,00", input);
        Assert.Contains("- Financeira Brb: R$ 6.500,00", input);
        Assert.Contains(model.Requests[0].CachedInstructions!, i => i.Contains("cada banco com o valor liberado nele, o total liberado e o reembolso"));
        Assert.Contains("Ainda não foi positivado", model.Requests[1].Input);
        Assert.Contains(model.Requests[0].CachedInstructions!, i => i.Contains("MODO VALOR LIBERADO"));

        // Nada saiu ainda: as duas esperam em Mensagens para aprovar, com a marca de margem e os dados do SIAPE.
        Assert.DoesNotContain(crm.Calls, c => c.Path == "/api/chat/send");
        var pending = (await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending").EnumerateArray().ToList();
        Assert.Equal(2, pending.Count);
        var lucinea = pending.Single(p => p.GetProperty("contact").GetString() == "Lucinea Ferreira");
        Assert.Equal("margem", lucinea.GetProperty("kind").GetString());
        Assert.Equal("5524999999967", lucinea.GetProperty("phone").GetString());
        var incoming = lucinea.GetProperty("incoming").GetString()!.Replace(' ', ' ');
        Assert.Contains("Valor liberado total no SIAPE: R$ 18.500,00 · reembolso R$ 1.234,56", incoming);
        Assert.Contains("BANCO DO BRASIL: R$ 12.000,00", incoming);
        Assert.Contains("MINISTERIO DA SAUDE", lucinea.GetProperty("incoming").GetString());

        // Aprovada: sai pela tela de conversa do CRM, na linha escolhida, e ganha a etiqueta.
        var approved = await owner.PostAsJsonAsync($"/api/v1/neural/crm/replies/{lucinea.GetProperty("id").GetGuid()}/approve", new { message = "Oii Lucinea, aqui é da Pontual 😊 Liberou margem nova. Quer simular?" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var sent = Assert.Single(crm.Calls, c => c.Path == "/api/chat/send");
        Assert.Contains("5524999999967@c.us", sent.Body);
        Assert.Contains("Siape_6", sent.Body);
        var tag = JsonDocument.Parse(Assert.Single(crm.Calls, c => c.Path == "/api/chat/tags/toggle").Body).RootElement;
        Assert.Equal(("5524999999967@c.us", "Siape_6"), (tag.GetProperty("chatId").GetString(), tag.GetProperty("botId").GetString()));

        // Não dá para "passar para atendente" uma primeira mensagem.
        var ana = pending.Single(p => p.GetProperty("contact").GetString() == "Ana Nova");
        var human = await owner.PostAsJsonAsync($"/api/v1/neural/crm/replies/{ana.GetProperty("id").GetGuid()}/human", new { message = "", userId = "8" });
        Assert.Equal(HttpStatusCode.Conflict, human.StatusCode);

        // Buscar de novo não oferece outra vez a quem já recebeu (ou já está na fila).
        var again = await (await owner.PostAsync("/api/v1/neural/siape/scan", null)).Json();
        Assert.Equal(0, again.GetProperty("created").GetInt32());
        Assert.Equal(2, model.Requests.Count);

        var overview = await owner.GetAsync("/api/v1/neural/siape").Json();
        Assert.Equal(2, overview.GetProperty("month").GetProperty("created").GetInt32());
        Assert.Equal(1, overview.GetProperty("month").GetProperty("sent").GetInt32());
        Assert.Equal(1, overview.GetProperty("month").GetProperty("pending").GetInt32());
        Assert.Contains(overview.GetProperty("lines").EnumerateArray(), l => l.GetProperty("id").GetString() == "Siape_6");
        Assert.Contains(overview.GetProperty("tables").GetProperty("list").EnumerateArray(), t => t.GetProperty("current").GetBoolean() && t.GetProperty("name").GetString() == "COMPLETA_NOVEMBRO_CORRIGIDA");
    }

    [Fact]
    public async Task Com_envio_direto_a_mensagem_sai_na_hora_e_respeita_o_maximo()
    {
        var (app, model, crm, siape, owner) = await SetupAsync(autoSend: true);
        using var _app = app;
        for (var i = 1; i <= 5; i++)
        {
            siape.Candidates.Add(new($"6550000000{i}", $"SERVIDOR {i}", $"6198237896{i}", 20000m - i * 100, 500m, "ORGAO", 45, "DF"));
            model.Decisions.Enqueue(Decide("responder", $"Oii Servidor {i}, aqui é da Pontual. Liberou margem nova!"));
        }

        var scan = await (await owner.PostAsync("/api/v1/neural/siape/scan", null)).Json();
        Assert.Equal(3, scan.GetProperty("created").GetInt32()); // o máximo da tela
        Assert.Equal(3, scan.GetProperty("sent").GetInt32());
        Assert.Equal(3, crm.Calls.Count(c => c.Path == "/api/chat/send"));
        Assert.Equal(3, model.Requests.Count);

        var recent = (await owner.GetAsync("/api/v1/neural/siape").Json()).GetProperty("recent").EnumerateArray().ToList();
        Assert.Equal(3, recent.Count);
        Assert.All(recent, r => Assert.Equal("sent", r.GetProperty("status").GetString()));
        Assert.All(recent, r => Assert.Equal("IA (envio automático)", r.GetProperty("reviewedBy").GetString()));
    }

    [Fact]
    public async Task Sem_linha_do_crm_ou_desligada_a_busca_explica_e_nao_gasta()
    {
        var (app, model, _, siape, owner) = await SetupAsync();
        using var _app = app;
        siape.Candidates.Add(new("65500000001", "LUCINEA", "24999999967", 1800m, 0m, "", 0, ""));
        await owner.PutAsJsonAsync("/api/v1/neural/siape/settings", new { server = "192.168.0.160,1433", database = "SIAPE", user = "pontual_acesso", botId = "", enabled = true, max = 3, day = 1 });
        var scan = await (await owner.PostAsync("/api/v1/neural/siape/scan", null)).Json();
        Assert.Contains("linha de WhatsApp", scan.GetProperty("message").GetString());
        Assert.Empty(model.Requests);

        // Dia inválido é recusado.
        var bad = await owner.PutAsJsonAsync("/api/v1/neural/siape/settings", new { server = "192.168.0.160,1433", database = "SIAPE", user = "x", day = 31 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
