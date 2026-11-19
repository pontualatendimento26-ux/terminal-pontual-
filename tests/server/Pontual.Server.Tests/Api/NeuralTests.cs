using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pontual.Server.Assistant;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Pontual.Server.Tests.Api;

public sealed class NeuralTests
{
    private const string Key = "sk-ant-chave-de-teste-0123456789";

    /// <summary>IA de mentira: guarda os pedidos, usa a ferramenta combinada (modo índice) e devolve o que foi mandado.</summary>
    private sealed class FakeBrain : IAssistantModel
    {
        public List<AssistantRequest> Requests { get; } = [];
        public List<AssistantJsonRequest> JsonRequests { get; } = [];
        public List<string> ToolResults { get; } = [];
        public string Answer { get; set; } = "Resposta da assistente.";
        public string Json { get; set; } = "{}";
        public (string Name, Dictionary<string, JsonElement> Input)? ToolCall { get; set; }

        public async Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Tools is { } tools && ToolCall is { } call)
            {
                var (result, status) = tools.Run(call.Name, call.Input);
                ToolResults.Add(result);
                if (status.Length > 0 && request.OnStatus is { } onStatus)
                {
                    await onStatus(status);
                }
            }

            await onText(Answer);
            return "end_turn";
        }

        public Task<string> AskJsonAsync(AssistantJsonRequest request, CancellationToken ct)
        {
            JsonRequests.Add(request);
            return Task.FromResult(Json);
        }
    }

    private static async Task<List<JsonElement>> AskAsync(HttpClient terminal, string text)
    {
        var response = await terminal.PostAsJsonAsync("/api/v1/device/assistant/messages", new { text });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {raw}");
        return [.. raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone())];
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient panel, string name, byte[] bytes, Guid? replace = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/neural/documents" + (replace is null ? "" : $"?replace={replace}"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        return panel.SendAsync(request);
    }

    private static byte[] Docx(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8);
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            foreach (var paragraph in paragraphs)
            {
                writer.Write($"<w:p><w:r><w:t>{paragraph}</w:t></w:r></w:p>");
            }

            writer.Write("</w:body></w:document>");
        }

        return stream.ToArray();
    }

    private static byte[] Pdf(string? text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        if (text is not null)
        {
            page.AddText(text, 11, new PdfPoint(30, 760), builder.AddStandard14Font(Standard14Font.Helvetica));
        }

        return builder.Build();
    }

    private static List<JsonElement> Items(JsonElement overview, string kind) =>
        [.. overview.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == kind)];

    [Fact]
    public async Task So_administrador_e_superadministrador_abrem_a_central_neural()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var admin = await app.AdminAsync(owner, "Carla", "admin");
        var support = await app.AdminAsync(owner, "Davi", "support");
        var auditor = await app.AdminAsync(owner, "Rita", "auditor");

        var overview = await owner.GetAsync("/api/v1/neural").Json();
        Assert.Equal(10, Items(overview, "script").Count);
        Assert.Equal("inline", overview.GetProperty("brain").GetProperty("mode").GetString());
        // O banco e a categoria dos roteiros iniciais saem do título.
        var finanto = Items(overview, "script").Single(i => i.GetProperty("title").GetString() == "Finanto - INSS");
        Assert.Equal("Finanto", finanto.GetProperty("bank").GetString());
        Assert.Equal("INSS", finanto.GetProperty("category").GetString());

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/neural")).StatusCode);
        foreach (var outsider in new[] { support, auditor })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/v1/neural")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/v1/neural/suggestions")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/v1/neural/search?q=safra")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Panel().GetAsync("/api/v1/neural")).StatusCode);
    }

    [Fact]
    public async Task Conhecimento_cadastrado_vai_para_a_ia_e_some_quando_desligado()
    {
        var model = new FakeBrain();
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });

        var created = await owner.PostAsJsonAsync("/api/v1/neural/items", new
        {
            kind = "knowledge", title = "Horário de corte das propostas", bank = "Pontual", category = "Procedimentos",
            keywords = "corte, horário", content = "Propostas digitadas até as 16h30 entram no mesmo dia.",
            aiInstructions = "Cite o horário exato.", notes = "Confirmado com a Carla.",
        }).Json();
        var id = created.GetProperty("id").GetGuid();

        await AskAsync(terminal, "Até que horas posso digitar?");
        var shared = model.Requests[^1].SharedInstructions;
        Assert.Null(model.Requests[^1].Tools);
        Assert.Contains("<conhecimento titulo=\"Horário de corte das propostas\" banco=\"Pontual\" categoria=\"Procedimentos\">", shared);
        Assert.Contains("Como usar este conteúdo: Cite o horário exato.", shared);
        Assert.Contains("Propostas digitadas até as 16h30", shared);
        // As observações internas não vão para a IA.
        Assert.DoesNotContain("Confirmado com a Carla", shared);

        var full = await owner.GetAsync($"/api/v1/neural/items/{id}").Json();
        Assert.Equal("Confirmado com a Carla.", full.GetProperty("notes").GetString());

        await owner.PutAsJsonAsync($"/api/v1/neural/items/{id}", new { enabled = false }).Json();
        await AskAsync(terminal, "E agora?");
        Assert.DoesNotContain("16h30", model.Requests[^1].SharedInstructions);

        // Sem texto não há conhecimento; tipo "documento" só entra por arquivo.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/neural/items", new { kind = "script", title = "Vazio", content = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/neural/items", new { kind = "document", title = "X", content = "y" })).StatusCode);

        // A busca não diferencia acento nem maiúscula e acha pelo corpo do texto.
        var search = await owner.GetAsync("/api/v1/neural/search?q=HORARIO%20digitadas").Json();
        Assert.Equal(id, search.GetProperty("items")[0].GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/neural/items/{id}")).StatusCode);
        Assert.Empty(Items(await owner.GetAsync("/api/v1/neural").Json(), "knowledge"));
    }

    [Fact]
    public async Task Documento_enviado_tem_o_texto_extraido_e_pdf_escaneado_fica_desligado()
    {
        var model = new FakeBrain();
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        var ids = new List<Guid>();
        try
        {
            var docx = await UploadAsync(owner, "Manual do Banco Água.docx", Docx("Regra de portabilidade", "O saldo mínimo é de R$ 2.000,00.")).Json();
            ids.Add(docx.GetProperty("id").GetGuid());
            Assert.Equal("document", docx.GetProperty("kind").GetString());
            Assert.Equal("Manual do Banco Água", docx.GetProperty("title").GetString());
            Assert.Equal("processed", docx.GetProperty("fileStatus").GetString());
            Assert.True(docx.GetProperty("enabled").GetBoolean());

            var txt = await UploadAsync(owner, "aviso.txt", Encoding.UTF8.GetBytes("A tabela nova vale a partir de segunda.")).Json();
            ids.Add(txt.GetProperty("id").GetGuid());

            var pdf = await UploadAsync(owner, "tabela.pdf", Pdf("Tabela de coeficientes do banco para o prazo de 84 meses com taxa reduzida")).Json();
            ids.Add(pdf.GetProperty("id").GetGuid());
            Assert.Equal("processed", pdf.GetProperty("fileStatus").GetString());

            // PDF sem texto (escaneado): guardado, desligado e sem fingir que foi aprendido.
            var scanned = await UploadAsync(owner, "contrato-escaneado.pdf", Pdf(null)).Json();
            var scannedId = scanned.GetProperty("id").GetGuid();
            ids.Add(scannedId);
            Assert.Equal("no_text", scanned.GetProperty("fileStatus").GetString());
            Assert.False(scanned.GetProperty("enabled").GetBoolean());
            Assert.Equal(0, scanned.GetProperty("chars").GetInt32());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v1/neural/items/{scannedId}", new { enabled = true })).StatusCode);

            await AskAsync(terminal, "Qual o saldo mínimo?");
            var shared = model.Requests[^1].SharedInstructions;
            Assert.Contains("<documento titulo=\"Manual do Banco Água\">", shared);
            Assert.Contains("O saldo mínimo é de R$ 2.000,00.", shared);
            Assert.Contains("A tabela nova vale a partir de segunda.", shared);
            Assert.Contains("84 meses", shared);
            Assert.DoesNotContain("contrato-escaneado", shared);

            // O arquivo original fica guardado e pode ser trocado.
            var original = await owner.GetAsync($"/api/v1/neural/items/{ids[1]}/file");
            Assert.Equal("A tabela nova vale a partir de segunda.", await original.Content.ReadAsStringAsync());
            var replaced = await UploadAsync(owner, "aviso-v2.txt", Encoding.UTF8.GetBytes("A tabela nova vale a partir de terça."), ids[1]).Json();
            Assert.Equal(ids[1], replaced.GetProperty("id").GetGuid());
            Assert.Equal("aviso-v2.txt", replaced.GetProperty("fileName").GetString());
            Assert.Contains("terça", (await owner.GetAsync($"/api/v1/neural/items/{ids[1]}").Json()).GetProperty("content").GetString());

            // Formato não aceito e arquivo corrompido são recusados, sem criar nada.
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, "planilha.xlsx", [1, 2, 3])).StatusCode);
            var broken = await UploadAsync(owner, "quebrado.docx", [1, 2, 3, 4]);
            Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
            Assert.Contains("corrompido", await broken.ErrorAsync());
            Assert.Equal(4, Items(await owner.GetAsync("/api/v1/neural").Json(), "document").Count);
        }
        finally
        {
            foreach (var id in ids)
            {
                await owner.DeleteAsync($"/api/v1/neural/items/{id}");
            }
        }
    }

    [Fact]
    public async Task Base_grande_vira_indice_e_a_ia_abre_so_o_que_precisa()
    {
        var model = new FakeBrain
        {
            ToolCall = (KnowledgeTools.Find, new Dictionary<string, JsonElement> { ["texto"] = JsonSerializer.SerializeToElement("carência especial") }),
        };
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (_, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });

        // Três conhecimentos longos passam do que cabe inteiro nas instruções.
        var filler = string.Join("\n\n", Enumerable.Range(1, 1100).Select(n => $"Parágrafo {n} do manual, com o texto de preenchimento usado só para dar tamanho ao teste."));
        for (var n = 1; n <= 3; n++)
        {
            var extra = n == 2 ? "\n\nA carência especial do Banco Lume é de 90 dias." : "";
            await owner.PostAsJsonAsync("/api/v1/neural/items", new { kind = "knowledge", title = $"Manual {n}", description = $"Resumo do manual {n}", content = filler + extra }).Json();
        }

        var overview = await owner.GetAsync("/api/v1/neural").Json();
        Assert.Equal("index", overview.GetProperty("brain").GetProperty("mode").GetString());

        var events = await AskAsync(terminal, "Qual a carência especial do Banco Lume?");
        var request = model.Requests[^1];
        Assert.NotNull(request.Tools);
        Assert.Contains("<indice>", request.SharedInstructions);
        Assert.Contains("Resumo do manual 2", request.SharedInstructions);
        Assert.DoesNotContain("carência especial do Banco Lume é de 90 dias", request.SharedInstructions);

        // A busca devolve o trecho certo, com o número do conhecimento no índice.
        var found = Assert.Single(model.ToolResults);
        Assert.Contains("Manual 2", found);
        Assert.Contains("90 dias", found);
        Assert.Contains(events, e => e.TryGetProperty("status", out var status) && status.GetString() == "Procurando na base…");

        // Abrir pelo número devolve a parte pedida, e não o conhecimento inteiro.
        var number = int.Parse(found[(found.IndexOf('[') + 1)..found.IndexOf(']')]);
        model.ToolCall = (KnowledgeTools.Open, new Dictionary<string, JsonElement>
        {
            ["numero"] = JsonSerializer.SerializeToElement(number),
            ["parte"] = JsonSerializer.SerializeToElement(1),
        });
        await AskAsync(terminal, "Abra o manual.");
        Assert.Contains("trecho=\"parte 1 de", model.ToolResults[^1]);
        Assert.True(model.ToolResults[^1].Length < KnowledgeLimits.Part + 500);
    }

    [Fact]
    public async Task Correcao_da_atendente_so_vira_conhecimento_depois_de_aprovada()
    {
        var model = new FakeBrain { Answer = "No Banco Lume o prazo máximo é 72 meses." };
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        var (_, other) = await app.ApprovedDeviceAsync(owner, "PC-02", "Joana Lima");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });

        var events = await AskAsync(terminal, "Qual o prazo máximo do Banco Lume?");
        var answerId = events[^1].GetProperty("id").GetInt64();

        // Outra máquina não avalia a resposta de Maria.
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/v1/device/assistant/feedback", new { messageId = answerId, helpful = false, text = "x" })).StatusCode);

        var sent = await terminal.PostAsJsonAsync("/api/v1/device/assistant/feedback", new { messageId = answerId, helpful = false, text = "Mudou: agora o prazo máximo é 84 meses." });
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        var state = await terminal.GetAsync("/api/v1/device/assistant").Json();
        Assert.False(state.GetProperty("feedback")[0].GetProperty("helpful").GetBoolean());

        var pending = Assert.Single((await owner.GetAsync("/api/v1/neural/suggestions?status=pending").Json()).EnumerateArray());
        var suggestionId = pending.GetProperty("id").GetGuid();
        Assert.Equal("correction", pending.GetProperty("kind").GetString());
        Assert.Equal("attendant", pending.GetProperty("source").GetString());
        Assert.Equal("Maria Souza (PC-01)", pending.GetProperty("attendant").GetString());
        Assert.Equal("Qual o prazo máximo do Banco Lume?", pending.GetProperty("question").GetString());
        Assert.Contains("72 meses", pending.GetProperty("answer").GetString());
        Assert.Equal(deviceId, pending.GetProperty("deviceId").GetGuid());

        // Enquanto está pendente, a IA não recebe a correção.
        await AskAsync(terminal, "De novo.");
        Assert.DoesNotContain("84 meses", model.Requests[^1].SharedInstructions);

        var approved = await owner.PostAsJsonAsync($"/api/v1/neural/suggestions/{suggestionId}/approve", new
        {
            createKind = "knowledge", title = "Banco Lume: prazo máximo", bank = "Banco Lume", content = "O prazo máximo do Banco Lume é de 84 meses.",
        }).Json();
        Assert.Equal("approved", approved.GetProperty("suggestion").GetProperty("status").GetString());
        Assert.Equal("atendentes", approved.GetProperty("item").GetProperty("source").GetString());

        await AskAsync(terminal, "E agora?");
        Assert.Contains("O prazo máximo do Banco Lume é de 84 meses.", model.Requests[^1].SharedInstructions);

        // Sugestão já revisada não é aprovada duas vezes; rejeitar e reabrir funcionam numa nova.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/v1/neural/suggestions/{suggestionId}/approve", new { createKind = "script", content = "x" })).StatusCode);

        var second = await AskAsync(terminal, "Outra pergunta");
        await terminal.PostAsJsonAsync("/api/v1/device/assistant/feedback", new { messageId = second[^1].GetProperty("id").GetInt64(), helpful = false, text = "Faltou falar da margem." });
        var again = Assert.Single((await owner.GetAsync("/api/v1/neural/suggestions?status=pending").Json()).EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal("rejected", (await owner.PostAsync($"/api/v1/neural/suggestions/{again}/reject", null).Json()).GetProperty("status").GetString());
        Assert.Equal("pending", (await owner.PostAsync($"/api/v1/neural/suggestions/{again}/reopen", null).Json()).GetProperty("status").GetString());

        var overview = await owner.GetAsync("/api/v1/neural").Json();
        Assert.Equal(1, overview.GetProperty("suggestions").GetProperty("pending").GetInt32());
        Assert.Equal(1, overview.GetProperty("suggestions").GetProperty("approved").GetInt32());
        Assert.Equal(2, overview.GetProperty("feedback").GetProperty("notHelpful").GetInt32());
    }

    [Fact]
    public async Task Analise_das_conversas_monta_o_perfil_e_sugere_sem_repetir_falas()
    {
        var model = new FakeBrain
        {
            Json = """
                {"perfil":{"resumo":"Atua com Finanto e Safra; pergunta sobre portabilidade.","estilo":"Perguntas curtas e diretas.","experiencia":"intermediaria",
                "bancos":[{"nome":"Finanto","perguntas":2}],"temas":["portabilidade"],"dificuldades":["regras de idade"],"pontos_fortes":["digitação"],"recomendacoes":["revisar tabela de idades"]},
                "sugestoes":[{"tipo":"gap","titulo":"Banco Lume: carência","detalhe":"Não há roteiro do Banco Lume.","banco":"Banco Lume","conteudo_sugerido":"","pergunta_exemplo":"Qual a carência do Lume?","ocorrencias":2,"sugestao_existente":0}]}
                """,
        };
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        await AskAsync(terminal, "Qual a carência do Lume?");
        await AskAsync(terminal, "E a idade máxima no Finanto?");

        var before = (await owner.GetAsync("/api/v1/neural").Json()).GetProperty("attendants")[0];
        Assert.Equal(4, before.GetProperty("newMessages").GetInt32());
        Assert.False(before.GetProperty("hasProfile").GetBoolean());

        var result = await owner.PostAsync($"/api/v1/neural/attendants/{deviceId}/analyze", null).Json();
        Assert.Equal(4, result.GetProperty("messagesRead").GetInt32());
        Assert.Equal(1, result.GetProperty("newSuggestions").GetInt32());

        var sentToModel = Assert.Single(model.JsonRequests);
        Assert.Contains("ATENDENTE: Qual a carência do Lume?", sentToModel.Input);
        Assert.Contains("<roteiro titulo=\"Finanto - INSS\"", sentToModel.Input);
        Assert.Equal("claude-opus-5-5", sentToModel.Model);

        var attendant = await owner.GetAsync($"/api/v1/neural/attendants/{deviceId}").Json();
        Assert.Equal("intermediaria", attendant.GetProperty("profile").GetProperty("experiencia").GetString());
        Assert.Equal(0, attendant.GetProperty("newMessages").GetInt32());
        Assert.Equal(4, attendant.GetProperty("messagesAnalyzed").GetInt32());

        // Sem falas novas, a IA não é chamada de novo.
        var idle = await owner.PostAsync($"/api/v1/neural/attendants/{deviceId}/analyze", null).Json();
        Assert.Equal(0, idle.GetProperty("messagesRead").GetInt32());
        Assert.Single(model.JsonRequests);

        // Na análise seguinte só as falas novas vão, com o perfil anterior, e a lacuna repetida soma em vez de duplicar.
        await AskAsync(terminal, "E a carência do Lume no cartão?");
        model.Json = model.Json.Replace("\"sugestao_existente\":0", "\"sugestao_existente\":1");
        var next = await owner.PostAsync($"/api/v1/neural/attendants/{deviceId}/analyze", null).Json();
        Assert.Equal(2, next.GetProperty("messagesRead").GetInt32());
        Assert.Equal(1, next.GetProperty("updatedSuggestions").GetInt32());
        Assert.DoesNotContain("idade máxima no Finanto", model.JsonRequests[^1].Input);
        Assert.Contains("\"experiencia\":\"intermediaria\"", model.JsonRequests[^1].Input);
        Assert.Contains("1. Banco Lume: carência", model.JsonRequests[^1].Input);

        var gap = Assert.Single((await owner.GetAsync("/api/v1/neural/suggestions?status=pending").Json()).EnumerateArray());
        Assert.Equal(4, gap.GetProperty("occurrences").GetInt32());

        var audit = (await owner.GetAsync("/api/v1/audit").Json()).GetRawText();
        Assert.Contains("mandou analisar 4 falas de Maria Souza", audit);
        Assert.Contains("leu o perfil de atendimento de Maria Souza", audit);
    }

    [Fact]
    public async Task Conversas_do_whatsapp_entram_na_analise_e_sao_apagadas_depois()
    {
        var model = new FakeBrain
        {
            Json = """
                {"perfil":{"resumo":"Atende portabilidade.","atendimento":"Responde rápido e pede o banco de origem logo no começo.","estilo":"Direta.","experiencia":"intermediaria",
                "bancos":[{"nome":"Banrisul","perguntas":1}],"temas":["portabilidade"],"dificuldades":[],"pontos_fortes":["conduz a conversa"],"recomendacoes":[]},
                "sugestoes":[{"tipo":"gap","titulo":"Portabilidade do Banrisul para maiores de 70","detalhe":"Cliente perguntou e a base não cobre.","banco":"Banrisul","conteudo_sugerido":"","pergunta_exemplo":"Com 70 anos posso portar do Banrisul?","ocorrencias":1,"sugestao_existente":0}]}
                """,
        };
        using var app = new TestApp { AssistantModel = model };
        var owner = await app.OwnerAsync();
        var (deviceId, terminal) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");

        static async Task<bool> LearningAsync(HttpClient client) =>
            (await client.PostAsJsonAsync("/api/v1/device/heartbeat", new { appsInUse = Array.Empty<string>(), sessionStartedAt = DateTime.UtcNow }).Json())
                .GetProperty("whatsAppLearning").GetBoolean();
        static Task<HttpResponseMessage> SendAsync(HttpClient client, string contact, string text) =>
            client.PostAsJsonAsync("/api/v1/device/assistant/whatsapp", new { contact, text });
        async Task<int> QueueAsync() => (await owner.GetAsync("/api/v1/neural").Json()).GetProperty("attendants")[0].GetProperty("whatsappPending").GetInt32();

        // Sem a chave da IA a assistente está desligada: nada é registrado.
        Assert.False(await LearningAsync(terminal));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(terminal, "João", "[10:00] Cliente: oi")).StatusCode);
        Assert.Equal(0, await QueueAsync());

        await owner.PutAsJsonAsync("/api/v1/assistant/settings", new { apiKey = Key });
        Assert.True(await LearningAsync(terminal));

        // O Terminal manda a conversa aberta várias vezes; o servidor emenda sem repetir mensagens.
        await SendAsync(terminal, "João da Silva", "[10:31] Cliente: quero portabilidade\n[10:32] Atendente: qual o seu banco?");
        await SendAsync(terminal, "João da Silva", "[10:32] Atendente: qual o seu banco?\n[10:33] Cliente: Banrisul, tenho 70 anos");
        await SendAsync(terminal, "João da Silva", "[10:31] Cliente: quero portabilidade\n[10:32] Atendente: qual o seu banco?");
        await SendAsync(terminal, "Ana Costa", "[11:00] Cliente: bom dia");
        Assert.Equal(2, await QueueAsync());

        var result = await owner.PostAsync($"/api/v1/neural/attendants/{deviceId}/analyze", null).Json();
        Assert.Equal(2, result.GetProperty("conversationsRead").GetInt32());
        Assert.Equal(0, result.GetProperty("messagesRead").GetInt32());
        Assert.Equal(1, result.GetProperty("newSuggestions").GetInt32());

        var input = Assert.Single(model.JsonRequests).Input;
        Assert.Contains("[10:31] Cliente: quero portabilidade\n[10:32] Atendente: qual o seu banco?\n[10:33] Cliente: Banrisul, tenho 70 anos\n</conversa>", input);
        Assert.Contains("[11:00] Cliente: bom dia", input);
        // Com a base pequena, o texto dos roteiros vai junto para a IA comparar com o que foi dito ao cliente.
        Assert.Contains("<roteiro titulo=\"Banrisul - INSS\"", input);
        Assert.Contains("Nenhuma pergunta nova.", input);

        // Lidas pela IA, as conversas saem do servidor; ficam o perfil e a sugestão pendente.
        Assert.Equal(0, await QueueAsync());
        var attendant = await owner.GetAsync($"/api/v1/neural/attendants/{deviceId}").Json();
        Assert.Equal(2, attendant.GetProperty("whatsappAnalyzed").GetInt32());
        Assert.StartsWith("Responde rápido", attendant.GetProperty("profile").GetProperty("atendimento").GetString());
        Assert.Single((await owner.GetAsync("/api/v1/neural/suggestions?status=pending").Json()).EnumerateArray());
        Assert.Contains("mandou analisar 2 conversas de WhatsApp de Maria Souza", (await owner.GetAsync("/api/v1/audit").Json()).GetRawText());

        // Sem nada novo, a IA não é chamada.
        await owner.PostAsync($"/api/v1/neural/attendants/{deviceId}/analyze", null).Json();
        Assert.Single(model.JsonRequests);

        // Desligado na Central: o Terminal é avisado, a fila é apagada e novos envios são descartados.
        await SendAsync(terminal, "Ana Costa", "[11:05] Cliente: ainda aí?");
        Assert.Equal(1, await QueueAsync());
        await owner.PutAsJsonAsync("/api/v1/neural/settings", new { whatsAppLearning = false });
        Assert.False(await LearningAsync(terminal));
        Assert.Equal(0, await QueueAsync());
        await SendAsync(terminal, "Ana Costa", "[11:06] Cliente: oi");
        Assert.Equal(0, await QueueAsync());
        Assert.Contains("desligou o registro das conversas do WhatsApp", (await owner.GetAsync("/api/v1/audit").Json()).GetRawText());
    }

    [Fact]
    public async Task Ligacoes_entre_conhecimentos_nao_se_repetem_e_somem_com_o_item()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var scripts = Items(await owner.GetAsync("/api/v1/neural").Json(), "script");
        var a = scripts[0].GetProperty("id").GetGuid();
        var b = scripts[1].GetProperty("id").GetGuid();

        var link = await owner.PostAsJsonAsync("/api/v1/neural/links", new { from = a, to = b }).Json();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/neural/links", new { from = b, to = a })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/neural/links", new { from = a, to = a })).StatusCode);

        var full = await owner.GetAsync($"/api/v1/neural/items/{a}").Json();
        Assert.Equal(b, Assert.Single(full.GetProperty("links").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(link.GetProperty("id").GetGuid(), full.GetProperty("links")[0].GetProperty("linkId").GetGuid());

        await owner.DeleteAsync($"/api/v1/neural/items/{b}");
        Assert.Empty((await owner.GetAsync("/api/v1/neural").Json()).GetProperty("links").EnumerateArray());
    }
}
