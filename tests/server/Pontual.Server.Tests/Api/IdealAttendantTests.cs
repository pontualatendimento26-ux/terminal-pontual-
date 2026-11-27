using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Tests.Api;

public sealed class IdealAttendantTests
{
    private const string Key = "sk-ant-chave-de-teste-0123456789";

    private const string Ideal =
        """
        {"resumo":"Atende rápido e explica com calma.","atendimento":"Abre com o nome do cliente e fecha com o próximo passo.",
        "qualidades":[{"titulo":"Agilidade","descricao":"Responde em poucos minutos.","atendentes":["Maria Souza"]},{"titulo":"Clareza","descricao":"Explica o contrato sem jargão.","atendentes":["Joana Lima"]}],
        "destaques":[{"atendente":"Maria Souza","ponto_forte":"Agilidade"},{"atendente":"Joana Lima","ponto_forte":"Clareza"}]}
        """;

    private sealed class FakeModel : IAssistantModel
    {
        public List<AssistantJsonRequest> JsonRequests { get; } = [];
        public string Json { get; set; } = Ideal;

        public async Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct)
        {
            await onText("ok");
            return "end_turn";
        }

        public Task<string> AskJsonAsync(AssistantJsonRequest request, CancellationToken ct)
        {
            JsonRequests.Add(request);
            return Task.FromResult(Json);
        }
    }

    private sealed class Later(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + ahead;
    }

    private static void AddProfile(TestApp app, Guid deviceId, string json, DateTime at)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
        db.AttendantInsights.Add(new AttendantInsight { DeviceId = deviceId, ProfileJson = json, UpdatedAt = at });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_atendente_ideal_junta_os_pontos_fortes_de_todas_e_deixa_as_dificuldades_de_fora()
    {
        var model = new FakeModel();
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (maria, _) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        var (joana, _) = await app.ApprovedDeviceAsync(owner, "PC-02", "Joana Lima");
        await app.ApprovedDeviceAsync(owner, "PC-03", "Carla Dias");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });

        AddProfile(app, maria, """{"resumo":"Atende rápido.","atendimento":"Responde em minutos.","pontos_fortes":["agilidade","pede o contracheque cedo"],"dificuldades":["portabilidade no Safra"],"recomendacoes":["revisar o Safra"]}""", DateTime.UtcNow.AddDays(-2));

        // Com um perfil só não há o que juntar, e a IA nem é chamada.
        var before = await owner.GetAsync("/api/v1/neural/ideal").Json();
        Assert.False(before.GetProperty("canGenerate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("profile").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync("/api/v1/neural/ideal/generate", null)).StatusCode);
        Assert.Empty(model.JsonRequests);
        Assert.False((await owner.GetAsync("/api/v1/neural").Json()).GetProperty("ideal").GetProperty("generated").GetBoolean());

        AddProfile(app, joana, """{"resumo":"Explica com calma.","pontos_fortes":["clareza"],"dificuldades":["demora a responder"]}""", DateTime.UtcNow.AddDays(-1));

        var result = await owner.PostAsync("/api/v1/neural/ideal/generate", null).Json();
        Assert.Equal("Atende rápido e explica com calma.", result.GetProperty("profile").GetProperty("resumo").GetString());
        Assert.Equal(["Joana Lima", "Maria Souza"], result.GetProperty("sources").EnumerateArray().Select(n => n.GetString()));

        // Vai o que funciona em cada uma; dificuldades e recomendações não entram. Quem não tem perfil fica de fora.
        var input = Assert.Single(model.JsonRequests).Input;
        Assert.Contains("<atendente nome=\"Maria Souza\">", input);
        Assert.Contains("Pontos fortes: agilidade; pede o contracheque cedo", input);
        Assert.Contains("<atendente nome=\"Joana Lima\">", input);
        Assert.Contains("Pontos fortes: clareza", input);
        Assert.DoesNotContain("Safra", input);
        Assert.DoesNotContain("demora a responder", input);
        Assert.DoesNotContain("Carla", input);

        // Fica guardado, aparece no mapa e a leitura vai para a auditoria.
        var saved = await owner.GetAsync("/api/v1/neural/ideal").Json();
        Assert.Equal(2, saved.GetProperty("profile").GetProperty("qualidades").GetArrayLength());
        Assert.Equal(0, saved.GetProperty("news").GetInt32());
        Assert.True((await owner.GetAsync("/api/v1/neural").Json()).GetProperty("ideal").GetProperty("generated").GetBoolean());
        var audit = (await owner.GetAsync("/api/v1/audit").Json()).GetRawText();
        Assert.Contains("mandou montar o perfil da atendente ideal com os perfis de 2 atendentes", audit);
        Assert.Contains("leu o perfil da atendente ideal", audit);

        // O Agente RH é só do superadministrador: o administrador comum não vê o círculo nem lê a atendente ideal.
        var admin = await app.AdminAsync(owner, "Paula", "admin");
        Assert.False((await admin.GetAsync("/api/v1/neural").Json()).GetProperty("rh").GetBoolean());
        Assert.True((await owner.GetAsync("/api/v1/neural").Json()).GetProperty("rh").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/neural/ideal")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v1/neural/ideal/generate", null)).StatusCode);

        // Um perfil atualizado depois da montagem é avisado, sem refazer sozinho.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            db.AttendantInsights.Single(i => i.DeviceId == joana).UpdatedAt = DateTime.UtcNow.AddHours(1);
            db.SaveChanges();
        }

        Assert.Equal(1, (await owner.GetAsync("/api/v1/neural/ideal").Json()).GetProperty("news").GetInt32());
        Assert.Single(model.JsonRequests);

        // A montagem automática espera o intervalo mínimo; passado ele, refaz sozinha porque um perfil mudou, e depois sossega.
        async Task<bool> AutoAsync(TimeProvider clock)
        {
            using var scope = app.Services.CreateScope();
            var services = scope.ServiceProvider;
            return await IdealAttendant.RunIfStaleAsync(
                services.GetRequiredService<PontualDb>(), services.GetRequiredService<IAssistantModel>(),
                services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                services.GetRequiredService<Pontual.Server.Api.AuditWriter>(), clock, CancellationToken.None);
        }

        Assert.False(await AutoAsync(TimeProvider.System));
        Assert.True(await AutoAsync(new Later(TimeSpan.FromHours(21))));
        Assert.Equal(2, model.JsonRequests.Count);
        Assert.False(await AutoAsync(new Later(TimeSpan.FromHours(60))));
        Assert.Contains("Sistema montou o perfil da atendente ideal", (await owner.GetAsync("/api/v1/audit").Json()).GetRawText());

        // Resposta torta da IA não apaga o perfil que já existia.
        model.Json = "{\"qualidades\":3}";
        Assert.Equal(HttpStatusCode.BadGateway, (await owner.PostAsync("/api/v1/neural/ideal/generate", null)).StatusCode);
        Assert.Equal("Atende rápido e explica com calma.", (await owner.GetAsync("/api/v1/neural/ideal").Json()).GetProperty("profile").GetProperty("resumo").GetString());
    }
}
