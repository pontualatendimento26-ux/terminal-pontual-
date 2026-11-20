using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pontual.Server.Assistant;

namespace Pontual.Server.Tests.Api;

public sealed class CoverageTests
{
    private const string Key = "sk-ant-chave-de-teste-0123456789";

    /// <summary>Segunda-feira, 10h em Brasília: dentro do horário da cobertura.</summary>
    private static readonly DateTimeOffset Monday = new(2026, 11, 23, 13, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>IA de mentira: devolve as decisões combinadas, na ordem, e guarda o que recebeu.</summary>
    private sealed class FakeModel : IAssistantModel
    {
        public Queue<object> Decisions { get; } = new();
        public List<AssistantJsonRequest> Requests { get; } = [];

        public Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct) =>
            throw new NotSupportedException("A cobertura não usa o chat.");

        public Task<string> AskJsonAsync(AssistantJsonRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(JsonSerializer.Serialize(Decisions.Dequeue()));
        }
    }

    private static object Decide(string acao, string mensagem, string aviso = "", string motivo = "teste") =>
        new { acao, mensagem, aviso_imediato = aviso, motivo };

    private static async Task<JsonElement> ReadAsync(HttpClient terminal, string contact, string text)
    {
        var response = await terminal.PostAsJsonAsync("/api/v1/device/assistant/coverage", new { contact, text });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Json();
    }

    private static async Task<string> HeartbeatCoverageAsync(HttpClient terminal) =>
        (await terminal.PostAsJsonAsync("/api/v1/device/heartbeat", new { appsInUse = Array.Empty<string>(), sessionStartedAt = DateTime.UtcNow }).Json())
            .GetProperty("coverage").GetString()!;

    private static async Task<(TestApp App, FakeModel Model, Clock Clock, HttpClient Owner, Guid DeviceId, HttpClient Terminal)> SetupAsync()
    {
        var model = new FakeModel();
        var clock = new Clock(Monday);
        var app = new TestApp { AssistantModel = model, Clock = clock };
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Gabi");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        return (app, model, clock, owner, deviceId, terminal);
    }

    [Fact]
    public void Mensagens_novas_sao_as_do_cliente_depois_da_ultima_resposta()
    {
        const string conversa = "[09:00] Cliente: Oi\n[09:01] Atendente: Bom dia!\n[09:05] Cliente: Mandei o extrato\ncontinua aqui\n[09:06] Cliente: Recebeu?";
        Assert.Equal("[09:05] Cliente: Mandei o extrato\ncontinua aqui\n[09:06] Cliente: Recebeu?", Coverage.Incoming(conversa));
        Assert.Equal("", Coverage.Incoming("[09:00] Cliente: Oi\n[09:01] Atendente: Bom dia!"));
        Assert.Equal("", Coverage.Incoming("texto solto sem falas"));
        Assert.Equal(Coverage.Fingerprint("Maria", "a"), Coverage.Fingerprint(" maria ", "a"));
        Assert.NotEqual(Coverage.Fingerprint("Maria", "a"), Coverage.Fingerprint("Maria", "b"));

        // Segunda a sexta, das 8h às 18h de Brasília (Monday = segunda, 10h).
        Assert.True(Coverage.WithinHours(Monday.UtcDateTime));
        Assert.False(Coverage.WithinHours(Monday.AddHours(-3).UtcDateTime)); // 7h
        Assert.True(Coverage.WithinHours(Monday.AddHours(7).AddMinutes(59).UtcDateTime)); // 17h59
        Assert.False(Coverage.WithinHours(Monday.AddHours(8).UtcDateTime)); // 18h
        Assert.True(Coverage.WithinHours(Monday.AddDays(4).UtcDateTime)); // sexta
        Assert.False(Coverage.WithinHours(Monday.AddDays(5).UtcDateTime)); // sábado
        Assert.False(Coverage.WithinHours(Monday.AddDays(-1).UtcDateTime)); // domingo
    }

    [Fact]
    public async Task Ligada_envia_o_simples_e_nao_responde_duas_vezes()
    {
        var (app, model, clock, owner, deviceId, terminal) = await SetupAsync();
        using var _ = app;

        // Desligada: o Terminal não recebe o modo e a rota não responde nada.
        Assert.Equal("", await HeartbeatCoverageAsync(terminal));
        Assert.Equal("skip", (await ReadAsync(terminal, "Maria", "[09:05] Cliente: Oi")).GetProperty("action").GetString());

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/neural/attendants/{deviceId}/coverage", new { mode = "on" })).StatusCode);
        Assert.Equal("on", await HeartbeatCoverageAsync(terminal));

        model.Decisions.Enqueue(Decide("enviar", "Recebi sim! Agora gere a autorização no SouGov."));
        const string conversa = "[09:00] Atendente: Me manda o extrato?\n[09:05] Cliente: Mandei o extrato";
        var first = await ReadAsync(terminal, "Maria", conversa);
        Assert.Equal("send", first.GetProperty("action").GetString());
        Assert.Equal("Recebi sim! Agora gere a autorização no SouGov.", first.GetProperty("message").GetString());
        Assert.False(first.GetProperty("holding").GetBoolean());

        // A IA recebe as regras da cobertura, o nome da atendente, a base e a conversa.
        var request = Assert.Single(model.Requests);
        Assert.Contains("MODO COBERTURA DE AUSÊNCIA", request.Instructions);
        Assert.Contains("A atendente Gabi está ausente", request.Instructions);
        Assert.Contains("Você está conversando com Gabi", request.Instructions);
        Assert.Contains("<mensagens_novas_do_cliente>\n[09:05] Cliente: Mandei o extrato", request.Input);

        var id = first.GetProperty("id").GetGuid();
        await terminal.PostAsJsonAsync($"/api/v1/device/assistant/coverage/{id}/result", new { sent = true });

        // A mesma fala não é respondida de novo, e a IA nem é chamada.
        Assert.Equal("done", (await ReadAsync(terminal, "Maria", conversa)).GetProperty("reason").GetString());
        Assert.Single(model.Requests);

        // Nova mensagem logo depois: espera o intervalo para não parecer robô.
        var mais = conversa + "\n[09:06] Atendente: Recebi sim!\n[09:06] Cliente: Ótimo";
        Assert.Equal("retry", (await ReadAsync(terminal, "Maria", mais)).GetProperty("action").GetString());

        // Um "ok" não precisa de resposta.
        clock.Now = clock.Now.AddMinutes(3);
        model.Decisions.Enqueue(Decide("nada", ""));
        Assert.Equal("skipped", (await ReadAsync(terminal, "Maria", mais)).GetProperty("reason").GetString());

        // Última fala da atendente: não há o que responder.
        Assert.Equal("answered", (await ReadAsync(terminal, "João", "[10:00] Cliente: Oi\n[10:01] Atendente: Oi!")).GetProperty("reason").GetString());

        var ficha = await owner.GetAsync($"/api/v1/neural/attendants/{deviceId}").Json();
        var replies = ficha.GetProperty("coverage").GetProperty("replies");
        Assert.Equal("on", ficha.GetProperty("coverage").GetProperty("mode").GetString());
        Assert.Equal(["skipped", "sent"], replies.EnumerateArray().Select(r => r.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Dinheiro_espera_aprovacao_com_aviso_na_hora()
    {
        var (app, model, _, owner, deviceId, terminal) = await SetupAsync();
        using var _app = app;
        await owner.PutAsJsonAsync($"/api/v1/neural/attendants/{deviceId}/coverage", new { mode = "on" });

        model.Decisions.Enqueue(Decide("fila", "O valor liberado ficou em R$ 3.000.", "Oi! Vou verificar aqui e já te retorno"));
        var read = await ReadAsync(terminal, "Ana", "[11:00] Cliente: Quanto vai cair?");
        Assert.Equal("send", read.GetProperty("action").GetString());
        Assert.True(read.GetProperty("holding").GetBoolean());
        Assert.Equal("Oi! Vou verificar aqui e já te retorno", read.GetProperty("message").GetString());
        var id = read.GetProperty("id").GetGuid();
        await terminal.PostAsJsonAsync($"/api/v1/device/assistant/coverage/{id}/result", new { sent = true, holding = true });

        // Nada aprovado ainda: a caixa de saída está vazia, e a resposta aparece como pendente.
        Assert.Equal(0, (await terminal.GetAsync("/api/v1/device/assistant/coverage/outbox").Json()).GetArrayLength());
        var overview = await owner.GetAsync("/api/v1/neural").Json();
        Assert.Equal(1, overview.GetProperty("attendants")[0].GetProperty("coveragePending").GetInt32());

        var approved = await owner.PostAsJsonAsync($"/api/v1/neural/coverage/{id}/approve", new { message = "O valor liberado ficou em R$ 2.950,00." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var outbox = await terminal.GetAsync("/api/v1/device/assistant/coverage/outbox").Json();
        var item = Assert.Single(outbox.EnumerateArray());
        Assert.Equal("Ana", item.GetProperty("contact").GetString());
        Assert.Equal("O valor liberado ficou em R$ 2.950,00.", item.GetProperty("message").GetString());

        await terminal.PostAsJsonAsync($"/api/v1/device/assistant/coverage/{id}/result", new { sent = true });
        Assert.Equal(0, (await terminal.GetAsync("/api/v1/device/assistant/coverage/outbox").Json()).GetArrayLength());
        var reply = (await owner.GetAsync($"/api/v1/neural/attendants/{deviceId}").Json()).GetProperty("coverage").GetProperty("replies")[0];
        Assert.Equal("sent", reply.GetProperty("status").GetString());
        Assert.Equal("Paulo", reply.GetProperty("reviewedBy").GetString());

        // Aprovar de novo não vale, e a auditoria registra quem aprovou.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/v1/neural/coverage/{id}/approve", new { })).StatusCode);
        Assert.Contains("aprovou uma resposta da cobertura", (await owner.GetAsync("/api/v1/audit").Json()).GetRawText());
    }

    [Fact]
    public async Task Ensaio_escreve_mas_nao_envia_e_fora_do_horario_nao_responde()
    {
        var (app, model, clock, owner, deviceId, terminal) = await SetupAsync();
        using var _ = app;
        await owner.PutAsJsonAsync($"/api/v1/neural/attendants/{deviceId}/coverage", new { mode = "test" });
        Assert.Equal("test", await HeartbeatCoverageAsync(terminal));

        model.Decisions.Enqueue(Decide("enviar", "Bom dia! Recebi seu documento."));
        var read = await ReadAsync(terminal, "Maria", "[09:05] Cliente: Mandei o RG");
        Assert.Equal("skip", read.GetProperty("action").GetString());
        Assert.Equal("draft", read.GetProperty("reason").GetString());
        var reply = (await owner.GetAsync($"/api/v1/neural/attendants/{deviceId}").Json()).GetProperty("coverage").GetProperty("replies")[0];
        Assert.Equal("Bom dia! Recebi seu documento.", reply.GetProperty("reply").GetString());
        Assert.Equal("send", reply.GetProperty("decision").GetString());

        // Domingo: o Terminal não recebe o modo e nada é respondido.
        clock.Now = Monday.AddDays(-1);
        Assert.Equal("", await HeartbeatCoverageAsync(terminal));
        Assert.Equal("off", (await ReadAsync(terminal, "Maria", "[09:05] Cliente: Oi de novo")).GetProperty("reason").GetString());
        Assert.Single(model.Requests);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v1/neural/attendants/{deviceId}/coverage", new { mode = "sempre" })).StatusCode);
    }
}
