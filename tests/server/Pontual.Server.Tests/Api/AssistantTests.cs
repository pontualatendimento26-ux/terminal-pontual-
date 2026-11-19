using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pontual.Server.Assistant;

namespace Pontual.Server.Tests.Api;

public sealed class AssistantTests
{
    private const string Key = "sk-ant-chave-de-teste-0123456789";

    /// <summary>IA de mentira: guarda o que recebeu e devolve a resposta combinada, palavra por palavra.</summary>
    private sealed class FakeModel : IAssistantModel
    {
        public List<AssistantRequest> Requests { get; } = [];
        public string Answer { get; set; } = "Siga o roteiro do banco.";
        public AssistantFailure? Failure { get; set; }

        public async Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct)
        {
            Requests.Add(request);
            if (Failure is not null)
            {
                throw Failure;
            }

            var words = Answer.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                await onText(i == 0 ? words[i] : " " + words[i]);
            }

            return "end_turn";
        }

        public Task<string> AskJsonAsync(AssistantJsonRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Estes testes não pedem análise.");
    }


    private static async Task<bool> AssistantOnAsync(HttpClient terminal) =>
        (await terminal.PostAsJsonAsync("/api/v1/device/heartbeat", new { appsInUse = Array.Empty<string>(), sessionStartedAt = DateTime.UtcNow }).Json())
            .GetProperty("assistant").GetBoolean();

    /// <summary>Envia a pergunta e devolve os eventos do fluxo (um JSON por linha "data:").</summary>
    private static async Task<List<JsonElement>> AskAsync(HttpClient terminal, string text, bool newConversation = false)
    {
        var response = await terminal.PostAsJsonAsync("/api/v1/device/assistant/messages", new { text, newConversation });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {raw}");
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return [.. raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone())];
    }

    private static string Answer(IEnumerable<JsonElement> events) =>
        string.Concat(events.Where(e => e.TryGetProperty("delta", out _)).Select(e => e.GetProperty("delta").GetString()));

    [Fact]
    public async Task Roteiros_iniciais_dos_bancos_entram_na_primeira_subida()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();

        var overview = await owner.GetAsync("/api/v1/assistant").Json();
        var titles = overview.GetProperty("scripts").EnumerateArray().Select(s => s.GetProperty("title").GetString()).ToList();
        Assert.Equal(10, titles.Count);
        Assert.Contains("Finanto - INSS", titles);
        Assert.Contains("BRB Red Consig (BRB Financeira) - INSS", titles);
        Assert.False(overview.GetProperty("settings").GetProperty("hasKey").GetBoolean());

        var safra = overview.GetProperty("scripts").EnumerateArray().Single(s => s.GetProperty("title").GetString() == "Safra - INSS");
        var full = await owner.GetAsync($"/api/v1/assistant/scripts/{safra.GetProperty("id").GetGuid()}").Json();
        Assert.Contains("Facta (925) exige 24 pagas", full.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Assistente_so_aparece_com_chave_e_perfil_ligado()
    {
        using var app = new TestApp { AssistantModel = new FakeModel() };
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");

        Assert.False(await AssistantOnAsync(terminal));
        Assert.Equal(HttpStatusCode.Conflict, (await terminal.PostAsJsonAsync("/api/v1/device/assistant/messages", new { text = "Oi" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key })).StatusCode);
        Assert.True(await AssistantOnAsync(terminal));

        // A chave nunca volta: nem para a Central, nem para o terminal.
        var overview = await owner.GetAsync("/api/v1/assistant");
        Assert.DoesNotContain(Key, await overview.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Key, await (await terminal.GetAsync("/api/v1/device/assistant")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v1/assistant/profiles/{deviceId}", new { enabled = false })).StatusCode);
        Assert.False(await AssistantOnAsync(terminal));
    }

    [Fact]
    public async Task Pergunta_leva_roteiros_perfil_e_historico_e_fica_registrada()
    {
        var model = new FakeModel { Answer = "No Finanto a taxa é **1,80% a.m.**" };
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        var (_, other) = await app.ApprovedDeviceAsync(owner, "PC-02", "Joana Lima");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key, instructions = "Encaminhe dúvidas fora do roteiro para a Carla." });
        await owner.PutAsJsonAsync($"/api/v1/assistant/profiles/{deviceId}", new { instructions = "Maria é nova: explique com mais detalhe." });

        var events = await AskAsync(terminal, "Qual a taxa da tabela especial do Finanto?\nSaldo de 5 mil.");
        Assert.Equal("No Finanto a taxa é **1,80% a.m.**", Answer(events));
        Assert.True(events[^1].GetProperty("done").GetBoolean());

        var first = Assert.Single(model.Requests);
        Assert.Equal(Key, first.ApiKey);
        Assert.Equal("claude-opus-5-5", first.Model);
        // Cada detalhe dos roteiros vai para a IA, junto com as orientações gerais.
        Assert.Contains("tabela especial com taxa de 1,80% a.m. e mínimo de 12 pagas", first.SharedInstructions);
        Assert.Contains("925 (9251/9252)", first.SharedInstructions);
        Assert.Contains("Encaminhe dúvidas fora do roteiro para a Carla.", first.SharedInstructions);
        // O perfil da atendente fica fora da parte comum (que é igual para todas).
        Assert.Contains("Maria Souza", first.PersonalInstructions);
        Assert.Contains("Maria é nova", first.PersonalInstructions);
        Assert.DoesNotContain("Maria", first.SharedInstructions);
        Assert.Equal("Qual a taxa da tabela especial do Finanto?\nSaldo de 5 mil.", Assert.Single(first.Turns).Text);

        // A segunda pergunta leva a conversa; outra atendente não vê nada disso.
        await AskAsync(terminal, "E o troco mínimo?");
        Assert.Equal(["user", "assistant", "user"], model.Requests[1].Turns.Select(t => t.Role));
        await AskAsync(other, "Oi");
        Assert.Single(model.Requests[2].Turns);
        Assert.Equal(model.Requests[0].SharedInstructions, model.Requests[2].SharedInstructions);
        Assert.DoesNotContain("Maria é nova", model.Requests[2].PersonalInstructions);

        var state = await terminal.GetAsync("/api/v1/device/assistant").Json();
        Assert.Equal(4, state.GetProperty("messages").GetArrayLength());
        Assert.Equal(2, (await other.GetAsync("/api/v1/device/assistant").Json()).GetProperty("messages").GetArrayLength());

        // "Nova conversa" zera o contexto que vai para a IA, sem apagar o histórico.
        await AskAsync(terminal, "Outra dúvida", newConversation: true);
        Assert.Single(model.Requests[3].Turns);

        // A Central lê as conversas, e a leitura entra na auditoria.
        var conversation = await owner.GetAsync($"/api/v1/assistant/conversations/{deviceId}").Json();
        Assert.Equal(6, conversation.GetArrayLength());
        Assert.Contains("leu as conversas de Maria Souza", (await owner.GetAsync("/api/v1/audit").Json()).GetRawText());
    }

    [Fact]
    public async Task Falha_da_IA_vira_mensagem_e_a_pergunta_seguinte_funciona()
    {
        var model = new FakeModel { Failure = new AssistantFailure("A chave da IA foi recusada. Avise o TI para conferir a chave na Central.") };
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });

        var failed = await AskAsync(terminal, "Primeira pergunta");
        Assert.StartsWith("A chave da IA foi recusada", failed[^1].GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadGateway, (await owner.PostAsync("/api/v1/assistant/test", null)).StatusCode);

        // A pergunta sem resposta é juntada à seguinte, para a conversa continuar alternando os lados.
        model.Failure = null;
        var events = await AskAsync(terminal, "Segunda pergunta");
        Assert.True(events[^1].GetProperty("done").GetBoolean());
        Assert.Equal("Primeira pergunta\n\nSegunda pergunta", Assert.Single(model.Requests[^1].Turns).Text);
    }

    [Fact]
    public async Task Permissoes_e_limites_da_central()
    {
        using var app = new TestApp { AssistantModel = new FakeModel() };
        var owner = await app.OwnerAsync();
        var admin = await app.AdminAsync(owner, "Bia", "admin");
        var support = await app.AdminAsync(owner, "Caio", "support");
        var (deviceId, _) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");

        // A chave é só do superadministrador; roteiros e perfis, dos administradores.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/assistant/settings", new { model = "claude-haiku-5-5" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/assistant/settings", new { model = "outro-modelo" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/api/v1/assistant")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync("/api/v1/assistant/scripts", new { title = "X", content = "Y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync($"/api/v1/assistant/conversations/{deviceId}")).StatusCode);

        // Roteiro novo, edição, desligar e excluir; nada é cortado em silêncio.
        var created = await admin.PostAsJsonAsync("/api/v1/assistant/scripts", new { title = "Banco Teste", content = "Linha 1\r\nLinha 2" }).Json();
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("Linha 1\nLinha 2", (await admin.GetAsync($"/api/v1/assistant/scripts/{id}").Json()).GetProperty("content").GetString());
        var tooLong = await admin.PutAsJsonAsync($"/api/v1/assistant/scripts/{id}", new { content = new string('a', 60_001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("Divida em duas partes", await tooLong.ErrorAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/assistant/scripts/{id}", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/assistant/scripts/{id}")).StatusCode);
        Assert.Equal(10, (await admin.GetAsync("/api/v1/assistant").Json()).GetProperty("scripts").GetArrayLength());

        // O texto do roteiro não vai para a auditoria; o fato, sim.
        var audit = (await owner.GetAsync("/api/v1/audit").Json()).GetRawText();
        Assert.Contains("cadastrou o roteiro", audit);
        Assert.DoesNotContain("Linha 1", audit);
    }
}
