using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Tests.Api;

public sealed class AssistantTests
{
    /// <summary>Carga inicial: um roteiro por .txt de roteiros-iniciais e um por convênio dos .json de convenios-iniciais.</summary>
    public static int InitialScripts
    {
        get
        {
            var texts = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "roteiros-iniciais"), "*.txt").Length;
            var agreements = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "convenios-iniciais"), "*.json")
                .Sum(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetArrayLength());
            return texts + agreements;
        }
    }

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

    [Fact]
    public async Task Tela_e_whatsapp_acompanham_a_pergunta_e_nao_ficam_gravados()
    {
        var model = new FakeModel();
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        Assert.True((await terminal.GetAsync("/api/v1/device/assistant").Json()).GetProperty("screen").GetBoolean());

        static async Task<List<JsonElement>> AskWithAsync(HttpClient client, object body)
        {
            var response = await client.PostAsJsonAsync("/api/v1/device/assistant/messages", body);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {raw}");
            return [.. raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone())];
        }

        object[] parts =
        [
            new { kind = "page", active = true, name = "Safra", address = "epfweb.safra.com.br/Proposta", title = "Nova proposta", text = "Erro: margem insuficiente" },
            new { kind = "whatsapp", active = false, name = "WhatsApp Web", contact = "João Cliente", text = "[10:32] Cliente: quero portabilidade, meu CPF é 123" },
        ];
        var jpeg = Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        var events = await AskWithAsync(terminal, new { text = "O que respondo para ele?", screen = new { image = jpeg, parts } });
        Assert.Equal("Safra, WhatsApp", events[0].GetProperty("screen").GetString());

        // A IA recebe o texto da tela, a conversa do WhatsApp e a foto, junto da pergunta e fora do histórico.
        var request = model.Requests[^1];
        Assert.Contains("<site nome=\"Safra\" em_primeiro_plano=\"sim\" endereco=\"epfweb.safra.com.br/Proposta\"", request.ScreenText);
        Assert.Contains("Erro: margem insuficiente", request.ScreenText);
        Assert.Contains("<whatsapp contato=\"João Cliente\">", request.ScreenText);
        Assert.Contains("quero portabilidade", request.ScreenText);
        Assert.Equal(7, request.ScreenImage!.Length);
        Assert.Equal("O que respondo para ele?", Assert.Single(request.Turns).Text);
        Assert.DoesNotContain("João", request.SharedInstructions);
        // A assistente sabe que vê a tela e que as conversas são usadas para ela aprender.
        Assert.Contains("você recebe a aba que ela está usando e a conversa do WhatsApp", request.SharedInstructions);
        Assert.Contains("Se perguntarem se você aprende com as conversas", request.SharedInstructions);
        Assert.Contains("comece a resposta com \"Sim\"", request.SharedInstructions);
        Assert.Contains("Se perguntarem se você consegue responder pela atendente, a resposta é sim", request.SharedInstructions);

        // A pergunta seguinte, sem tela, não carrega a tela anterior.
        await AskAsync(terminal, "E depois?");
        Assert.Null(model.Requests[^1].ScreenText);
        Assert.Null(model.Requests[^1].ScreenImage);
        Assert.DoesNotContain(model.Requests[^1].Turns, t => t.Text.Contains("portabilidade"));

        // No histórico fica só o nome do que estava aberto, nunca o conteúdo da tela.
        var history = (await owner.GetAsync($"/api/v1/assistant/conversations/{deviceId}").Json()).GetRawText();
        Assert.Contains("Safra, WhatsApp", history);
        Assert.DoesNotContain("margem insuficiente", history);
        Assert.DoesNotContain("portabilidade", history);
        Assert.DoesNotContain("Cliente", history);

        // Foto que não é JPEG é descartada; o texto segue.
        await AskWithAsync(terminal, new { text = "E esta?", screen = new { image = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2]), parts } });
        Assert.Null(model.Requests[^1].ScreenImage);
        Assert.NotNull(model.Requests[^1].ScreenText);

        // Desligado na Central, o servidor ignora a tela mesmo que o Terminal a mande.
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { screenContext = false });
        Assert.False((await terminal.GetAsync("/api/v1/device/assistant").Json()).GetProperty("screen").GetBoolean());
        var off = await AskWithAsync(terminal, new { text = "E agora?", screen = new { image = jpeg, parts } });
        Assert.Equal("", off[0].GetProperty("screen").GetString());
        Assert.Null(model.Requests[^1].ScreenText);
        Assert.Null(model.Requests[^1].ScreenImage);
        Assert.Contains("você não recebe a tela nem o WhatsApp", model.Requests[^1].SharedInstructions);
        Assert.Contains("proibiu a assistente de ver a tela", (await owner.GetAsync("/api/v1/audit").Json()).GetRawText());
    }

    [Fact]
    public async Task Texto_salvo_no_perfil_volta_pela_palavra_chave_sem_chamar_a_ia()
    {
        var model = new FakeModel();
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (_, maria) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        async Task<string> Say(HttpClient who, string text, bool fresh = false) => Answer(await AskAsync(who, text, fresh));

        // Passo a passo.
        Assert.Equal(SavedTexts.AskText, await Say(maria, "eu quero que salve isso no meu perfil"));
        Assert.Equal(SavedTexts.AskKeyword, await Say(maria, "Olá! Segue o PIX:\nchave 123"));
        Assert.Equal(SavedTexts.BadKeyword, await Say(maria, "Minhas palavras-chave"));
        Assert.Contains("salvei", await Say(maria, "Saudação"));
        Assert.Equal("Olá! Segue o PIX:\nchave 123", await Say(maria, "SAUDACAO!", fresh: true));

        // Tudo junto; texto e palavra numa mensagem só; palavra-chave antes do texto.
        await Say(maria, "Quero que salve isso no meu perfil: Bom dia, tudo bem?\nPalavra-chave: bd");
        Assert.Equal("Bom dia, tudo bem?", await Say(maria, "bd"));
        await Say(maria, "salva no meu perfil");
        await Say(maria, "Texto: Boa tarde\na palavra chave é bt");
        Assert.Equal("Boa tarde", await Say(maria, "bt"));
        await Say(maria, "salvar no meu perfil");
        Assert.Equal(SavedTexts.AskText, await Say(maria, "palavra-chave: bn"));
        await Say(maria, "Boa noite");
        Assert.Equal("Boa noite", await Say(maria, "bn"));

        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Textos_salvos_podem_ser_trocados_listados_e_apagados_e_sao_so_da_atendente()
    {
        var model = new FakeModel();
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (_, maria) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        var (_, ana) = await app.ApprovedDeviceAsync(owner, "PC-02", "Ana Lima");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        async Task<string> Say(HttpClient who, string text) => Answer(await AskAsync(who, text));
        await Say(maria, "salvar no meu perfil: Bom dia\npalavra-chave: bd");
        await Say(maria, "salvar no meu perfil: Boa tarde\npalavra-chave: bt");

        Assert.Contains("troquei", await Say(maria, "salvar no meu perfil: Oi!\npalavra-chave: BD"));
        Assert.Equal("Oi!", await Say(maria, "bd"));
        Assert.Contains("- BD", await Say(maria, "minhas palavras-chave"));
        Assert.Contains("apaguei", await Say(maria, "apagar palavra-chave bt"));
        await Say(maria, "salvar no meu perfil");
        Assert.Contains("não salvei", await Say(maria, "cancelar"));
        Assert.Empty(model.Requests);

        // Vai para a IA: palavra apagada, palavra dentro de uma pergunta, fala comum e palavra de outra atendente.
        await Say(maria, "bt");
        await Say(maria, "o que é bd?");
        await Say(maria, "cancelar");
        await Say(maria, "como salvar no meu perfil do INSS");
        await Say(ana, "bd");
        Assert.Equal(5, model.Requests.Count);
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
        Assert.Equal(InitialScripts, titles.Count);
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
    public async Task O_que_a_analise_aprendeu_de_uma_atendente_vai_so_para_ela()
    {
        var model = new FakeModel();
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (deviceId, maria) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        var (_, joana) = await app.ApprovedDeviceAsync(owner, "PC-02", "Joana Lima");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
            db.AttendantInsights.Add(new AttendantInsight
            {
                DeviceId = deviceId,
                ProfileJson = """{"resumo":"Atende rápido.","experiencia":"indefinida","dificuldades":["portabilidade no Safra","margem negativa"],"temas":[],"bancos":[{"nome":"BRB","perguntas":3},{"nome":"Inter","perguntas":1}]}""",
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        await AskAsync(maria, "Oi");
        await AskAsync(joana, "Oi");

        var personal = model.Requests[0].PersonalInstructions;
        Assert.Contains("Resumo: Atende rápido.", personal);
        Assert.Contains("Dificuldades: portabilidade no Safra; margem negativa", personal);
        Assert.DoesNotContain("indefinida", personal);
        Assert.Contains("Nunca imite o que o perfil aponta como dificuldade", personal);
        // A regra de respeito ao cliente vale para todas as atendentes.
        Assert.Contains("Com o cliente, o tom é sempre respeitoso e profissional", model.Requests[0].SharedInstructions);
        Assert.Contains("não escreva essa resposta", model.Requests[1].SharedInstructions);
        Assert.Contains("Bancos com que mais trabalha: BRB; Inter", personal);
        Assert.DoesNotContain("portabilidade no Safra", model.Requests[0].SharedInstructions);
        // A outra atendente não recebe nada do perfil da Maria.
        Assert.DoesNotContain("perfil_da_atendente", model.Requests[1].PersonalInstructions);
        Assert.DoesNotContain("Safra", model.Requests[1].PersonalInstructions);

        Assert.Equal("", AssistantPrompt.Learned("isto não é json"));
        Assert.Equal("", AssistantPrompt.Learned(null));
    }

    [Fact]
    public async Task Lista_do_que_o_terminal_pode_abrir_vai_com_a_pergunta()
    {
        var model = new FakeModel { Answer = "Abrindo o WhatsApp.\n[[abrir: WhatsApp]]" };
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });

        var response = await terminal.PostAsJsonAsync("/api/v1/device/assistant/messages", new
        {
            text = "abre o whatsapp",
            apps = new object[] { new { name = "WhatsApp", kind = "site" }, new { name = "Google", kind = "site" }, new { name = "Excel", kind = "program" }, new { name = "", kind = "site" } },
        });
        Assert.True(response.IsSuccessStatusCode);
        await response.Content.ReadAsStringAsync();

        var request = Assert.Single(model.Requests);
        Assert.Equal("<terminal_pode_abrir>\nWhatsApp\nGoogle\nExcel (programa)\n</terminal_pode_abrir>", request.ScreenText);
        Assert.Contains("[[abrir: nome exato da lista]]", request.SharedInstructions);

        // Sem a lista (Terminal antigo), nada vai junto.
        await AskAsync(terminal, "abre o whatsapp");
        Assert.Null(model.Requests[1].ScreenText);
    }

    [Fact]
    public void Perfil_importado_vale_para_a_atendente_de_mesmo_nome()
    {
        var settings = new Dictionary<string, string>
        {
            [TextSetting.AssistantImportedProfiles] = """{"Gabriela":{"resumo":"G"},"Ana":{"resumo":"A"},"Ana Paula":{"resumo":"AP"},"Fernanda":{"resumo":"F"}}""",
        };

        Assert.Contains("\"G\"", AssistantPrompt.ImportedProfile(settings, "GABI"));
        Assert.Contains("\"G\"", AssistantPrompt.ImportedProfile(settings, "Gabriela Souza"));
        Assert.Contains("\"F\"", AssistantPrompt.ImportedProfile(settings, "fernanda"));
        // "Ana" bate exato; "An" é curto demais; nome sem perfil não recebe o de ninguém.
        Assert.Contains("\"A\"", AssistantPrompt.ImportedProfile(settings, "Ana"));
        Assert.Equal("", AssistantPrompt.ImportedProfile(settings, "An"));
        Assert.Equal("", AssistantPrompt.ImportedProfile(settings, "Sarah"));
        Assert.Equal("", AssistantPrompt.ImportedProfile(settings, null));
        Assert.Equal("", AssistantPrompt.ImportedProfile(new Dictionary<string, string>(), "Ana"));
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
        Assert.Equal(InitialScripts, (await admin.GetAsync("/api/v1/assistant").Json()).GetProperty("scripts").GetArrayLength());

        // O texto do roteiro não vai para a auditoria; o fato, sim.
        var audit = (await owner.GetAsync("/api/v1/audit").Json()).GetRawText();
        Assert.Contains("cadastrou o roteiro", audit);
        Assert.DoesNotContain("Linha 1", audit);
    }
}
