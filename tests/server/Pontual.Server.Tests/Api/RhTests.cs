using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pontual.Server.Data;
using Pontual.Server.Rh;

namespace Pontual.Server.Tests.Api;

/// <summary>
/// Agente RH, etapa 2: o anonimizador e a porta para a Central. Todos os nomes, CPFs, telefones e contratos
/// daqui são FICTÍCIOS, inventados para o teste.
/// </summary>
public sealed class RhTests
{
    // CPF fictício com dígitos verificadores válidos (o clássico de exemplo).
    private const string FakeCpf = "52998224725";

    [Theory]
    [InlineData("Meu CPF é 529.982.247-25, pode consultar", "Meu CPF é [CPF], pode consultar")]
    [InlineData("cpf 52998224725", "cpf [CPF]")]
    [InlineData("me liga no (61) 99876-5432 ou 61998765432", "me liga no [TELEFONE] ou [TELEFONE]")]
    [InlineData("whats +55 61 9 9876 5432", "whats [TELEFONE]")]
    [InlineData("contrato 0123456789012 do benefício 123.456.789-0", "contrato [NUMERO] do benefício [NUMERO]")]
    [InlineData("manda no email joao.silva@exemplo.com.br", "manda no email [EMAIL]")]
    [InlineData("CNPJ 12.345.678/0001-90", "CNPJ [CNPJ]")]
    [InlineData("matrícula-123456789 e benefício:.9876543210123", "matrícula-[NUMERO] e benefício:.[NUMERO]")]
    [InlineData("Libera R$ 12.345,67 em 84x, parcela de 310,50, até 27/11/2026 às 10:30", "Libera R$ 12.345,67 em 84x, parcela de 310,50, até 27/11/2026 às 10:30")]
    [InlineData("saldo de 150000,00 e margem 420", "saldo de 150000,00 e margem 420")]
    [InlineData("Boa tarde, me chamo Roberto Carlos de Souza", "Boa tarde, me chamo [NOME]")]
    public void Anonimizador_tira_dado_pessoal_e_deixa_valor_data_e_hora(string text, string expected) =>
        Assert.Equal(expected, Anonymizer.Clean(text));

    [Fact]
    public void Anonimizador_tira_os_nomes_que_a_Central_conhece()
    {
        var clean = Anonymizer.Clean(
            "Oi Kamila, aqui é o José da Silva Júnior. A JOSE já mandou o contracheque para a Sarah?",
            clientNames: ["Jose da Silva Junior"], staffNames: ["Kamila Oliveira Borges", "SARAH"]);

        Assert.Equal("Oi [ATENDENTE], aqui é o [CLIENTE]. A [CLIENTE] já mandou o contracheque para a [ATENDENTE]?", clean);
        // Palavra comum que só parece nome no cadastro do CRM não é apagada do texto.
        Assert.Equal("o cliente novo pediu", Anonymizer.Clean("o cliente novo pediu", clientNames: ["Cliente Novo 5561"]));
    }

    private static void Seed(TestApp app, Action<PontualDb> seed)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PontualDb>();
        seed(db);
        db.SaveChanges();
    }

    [Fact]
    public async Task Agente_RH_le_a_Central_pela_porta_dele_e_os_casos_chegam_anonimos()
    {
        using var app = new TestApp();
        var owner = await app.OwnerAsync();
        var (maria, _) = await app.ApprovedDeviceAsync(owner, "PC-01", "Maria Souza");
        var (joana, _) = await app.ApprovedDeviceAsync(owner, "PC-02", "Joana Lima");

        // A Central já nasce com conhecimentos de fábrica: a conta é feita a partir deles.
        var baseline = (await owner.GetAsync("/api/v1/rh").Json()).GetProperty("knowledge").GetInt32();
        var open = await owner.PostAsJsonAsync("/api/v1/neural/items", new { kind = "script", title = "Roteiro Zebrafixo", content = "No produto zebrafixo, peça o contracheque e o extrato de empréstimos.", enabled = true });
        Assert.True(open.IsSuccessStatusCode, await open.Content.ReadAsStringAsync());
        var secret = await owner.PostAsJsonAsync("/api/v1/neural/items", new { kind = "knowledge", title = "Senha do portal", content = "Zebrafixo: login do portal é usuario-ficticio e a senha é segredo-ficticio.", enabled = true, staffOnly = true });
        Assert.True(secret.IsSuccessStatusCode, await secret.Content.ReadAsStringAsync());

        var at = DateTime.UtcNow.AddDays(-2);
        Seed(app, db =>
        {
            db.AttendantInsights.Add(new AttendantInsight { DeviceId = maria, UpdatedAt = at, ProfileJson = """{"resumo":"Atende rápido.","pontos_fortes":["agilidade","pede o contracheque cedo"],"dificuldades":["portabilidade no Safra"]}""" });
            db.AttendantInsights.Add(new AttendantInsight { DeviceId = joana, UpdatedAt = at, ProfileJson = """{"resumo":"Explica com calma.","pontos_fortes":["clareza"]}""" });

            // Um atendimento fictício, com tudo o que não pode chegar ao agente.
            void Line(int minute, string author, string sender, string text) => db.CrmEvents.Add(new CrmEvent
            {
                At = at.AddMinutes(minute), ConversationId = "9001", Contact = "Antonio Pereira Gomes", Phone = "5561998765432",
                Author = author, Sender = sender, Text = text,
            });
            Line(0, CrmAuthor.Client, "", "Boa tarde, sou o Antonio Pereira, quero portar meu consignado");
            Line(1, CrmAuthor.Team, "Maria Souza Ribeiro", "Oi Antonio! Aqui é a Maria. Me passa seu CPF?");
            Line(2, CrmAuthor.Client, "", $"{FakeCpf}, meu telefone é (61) 99876-5432 e o contrato é 770012345678");
            Line(3, CrmAuthor.Team, "Maria Souza Ribeiro", "Obrigada, Antonio. Libera R$ 8.450,00 em 84x. Posso seguir?");
            // Conversa curta demais (só o cliente escreveu) não vira caso.
            db.CrmEvents.Add(new CrmEvent { At = at, ConversationId = "9002", Contact = "Outro Contato", Author = CrmAuthor.Client, Text = "oi" });
        });

        // Só o superadministrador passa pela porta.
        var admin = await app.AdminAsync(owner, "Paula", "admin");
        foreach (var path in (string[])["/api/v1/rh", "/api/v1/rh/strengths", "/api/v1/rh/knowledge?q=zebrafixo", "/api/v1/rh/cases"])
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(path)).StatusCode);
        }

        var status = await owner.GetAsync("/api/v1/rh").Json();
        Assert.Equal(2, status.GetProperty("attendants").GetInt32());
        Assert.Equal(3, status.GetProperty("strengths").GetInt32());
        Assert.Equal(baseline + 1, status.GetProperty("knowledge").GetInt32());
        Assert.Equal(1, status.GetProperty("cases").GetInt32());

        // 1. Pontos fortes, com o nome de cada atendente e sem as dificuldades.
        var strengths = await owner.GetAsync("/api/v1/rh/strengths").Json();
        var first = strengths.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Maria Souza");
        Assert.Equal(["agilidade", "pede o contracheque cedo"], first.GetProperty("strengths").EnumerateArray().Select(s => s.GetString()));
        Assert.DoesNotContain("Safra", strengths.GetRawText());

        // 2. Conhecimento da base; o que é só para atendentes (logins, senhas) não passa, nem pela busca nem abrindo direto.
        var hit = Assert.Single((await owner.GetAsync("/api/v1/rh/knowledge?q=zebrafixo").Json()).EnumerateArray());
        Assert.Equal("Roteiro Zebrafixo", hit.GetProperty("title").GetString());
        Assert.Contains("peça o contracheque", (await owner.GetAsync($"/api/v1/rh/knowledge/{hit.GetProperty("id").GetGuid()}").Json()).GetProperty("content").GetString());
        var secretId = (await secret.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/rh/knowledge/{secretId}")).StatusCode);

        // 3. Casos: o código não é o número da conversa, e o texto chega sem nome, CPF, telefone nem contrato.
        var listed = Assert.Single((await owner.GetAsync("/api/v1/rh/cases").Json()).EnumerateArray());
        var id = listed.GetProperty("id").GetString()!;
        Assert.StartsWith("caso-", id);
        Assert.DoesNotContain("9001", id);
        Assert.Equal(2, listed.GetProperty("clientLines").GetInt32());

        var text = (await owner.GetAsync($"/api/v1/rh/cases/{id}").Json()).GetProperty("text").GetString()!;
        Assert.Contains("Cliente: Boa tarde, sou o [CLIENTE], quero portar meu consignado", text);
        Assert.Contains("Atendente: Oi [CLIENTE]! Aqui é a [ATENDENTE]. Me passa seu CPF?", text);
        Assert.Contains("Cliente: [CPF], meu telefone é [TELEFONE] e o contrato é [NUMERO]", text);
        Assert.Contains("Libera R$ 8.450,00 em 84x", text);
        Assert.StartsWith("[dia 1 ", text);
        foreach (var leaked in (string[])["Antonio", "Pereira", "Maria", FakeCpf, "99876", "770012345678", at.Year.ToString()])
        {
            Assert.DoesNotContain(leaked, text);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/rh/cases/caso-000000000000")).StatusCode);

        // Leitura de pontos fortes e de caso fica na auditoria.
        var audit = (await owner.GetAsync("/api/v1/audit").Json()).GetRawText();
        Assert.Contains("leu os pontos fortes das atendentes pelo Agente RH", audit);
        Assert.Contains($"leu um caso de atendimento anônimo ({id}) pelo Agente RH", audit);
    }
}
