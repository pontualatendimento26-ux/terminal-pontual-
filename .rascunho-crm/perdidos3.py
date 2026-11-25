import io
p='tests/server/Pontual.Server.Tests/Api/CrmTests.cs'
s=io.open(p,encoding='utf-8').read()
def rep(a,b):
    global s
    assert s.count(a)==1,(a,s.count(a))
    s=s.replace(a,b)

rep('''        public bool TagCreated { get; set; }
''','''        public bool TagCreated { get; set; }
        /// <summary>Conversas da tela do CRM (rota /api/chat/contacts), em JSON.</summary>
        public object[] Chats { get; set; } = [];
        /// <summary>Histórico de cada chat|linha (rota /api/chat/messages).</summary>
        public Dictionary<string, object[]> History { get; } = [];
''')
rep('''            object answer = path switch
            {
                "/login" => new { success = true, token = "token-do-agente" },''','''            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            object answer = path switch
            {
                "/login" => new { success = true, token = "token-do-agente" },
                "/api/sessions" => new object[] { new { id = "Kamila_4", canal_id = 4 }, new { id = "Pedro_7", canal_id = 7 } },
                "/api/chat/contacts" => new { success = true, chats = query["page"] == "1" ? Chats : [], temMais = false },
                "/api/chat/messages" => new { success = true, messages = History.GetValueOrDefault($"{query["chatId"]}|{query["botId"]}", []) },
                "/api/chat/send" => new { success = true },''')

test = '''
    private static object Canal(string bot, DateTime brasilia, bool fromMe, string wpp, int responsavel = 8) =>
        new { bot_id = bot, from_me = fromMe, ts = brasilia.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"), responsavel_id = responsavel, ultima = new { wpp } };

    private static object Msg(string wpp, DateTime utc, bool fromMe, string body, string who = "", string type = "chat") =>
        new { wpp_id = wpp, timestamp = new DateTimeOffset(utc).ToUnixTimeMilliseconds(), is_from_me = fromMe, body, sent_by_name = who, type };

    [Fact]
    public async Task Acha_os_clientes_perdidos_e_responde_no_jeito_da_dona()
    {
        var (app, model, crm, owner, _) = await SetupAsync();
        using var _app = app;
        var now = DateTime.UtcNow;
        DateTime Br(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, AssistantPrompt.BrazilTime);
        var tresDias = now.AddDays(-3);
        crm.Chats =
        [
            new { chat_id = "5521983574865@c.us", name = "LUCINEA", last_message_at = tresDias.ToString("O"), canais = new[] { Canal("Kamila_4", Br(tresDias), false, "W1") } },
            new { chat_id = "5561000000001@c.us", name = "SÓ OK", last_message_at = tresDias.ToString("O"), canais = new[] { Canal("Kamila_4", Br(tresDias), false, "W2") } },
            new { chat_id = "5561000000002@c.us", name = "HOJE", last_message_at = now.AddHours(-2).ToString("O"), canais = new[] { Canal("Kamila_4", Br(now.AddHours(-2)), false, "W3") } },
            new { chat_id = "5561000000003@c.us", name = "OUTRO FUNIL", last_message_at = tresDias.ToString("O"), canais = new[] { Canal("Pedro_7", Br(tresDias), false, "W4") } },
        ];
        crm.History["5521983574865@c.us|Kamila_4"] =
        [
            Msg("A0", tresDias.AddDays(-1), true, "Oii Lucinea, tudo bem? 😊", "Kamila Oliveira Borges"),
            Msg("S1", tresDias.AddMinutes(-1), true, "A automação adicionou a etiqueta", "Sistema", "notification_template"),
            Msg("W1", tresDias, false, "Como faço para simular o crédito consignado?"),
        ];
        crm.History["5561000000001@c.us|Kamila_4"] = [Msg("W2", tresDias, false, "ok")];

        model.Decisions.Enqueue(Decide("responder", "Oii Lucinea! Desculpa a demora 😊 Me manda seu CPF que eu simulo pra você?"));
        model.Decisions.Enqueue(Decide("nada", "", "só agradeceu"));
        var scan = await (await owner.PostAsync("/api/v1/neural/crm/lost/scan", null)).Json();
        Assert.Equal(1, scan.GetProperty("created").GetInt32());

        // A IA leu só os dois de 3 dias, no funil 4; soube que a dona é a Kamila e viu o jeito dela na conversa.
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("<atendente>Kamila Oliveira Borges</atendente>", model.Requests[0].Input);
        Assert.Contains("(Kamila Oliveira Borges) Oii Lucinea", model.Requests[0].Input);
        Assert.DoesNotContain("automação", model.Requests[0].Input);
        Assert.Contains(model.Requests[0].CachedInstructions!, i => i.Contains("MODO CLIENTE SEM RESPOSTA"));

        var pending = Assert.Single((await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending").EnumerateArray());
        Assert.Equal(("perdido", "Kamila Oliveira Borges"), (pending.GetProperty("kind").GetString(), pending.GetProperty("attendant").GetString()));

        // Buscar de novo não lê as mesmas conversas outra vez.
        await owner.PostAsync("/api/v1/neural/crm/lost/scan", null);
        Assert.Equal(2, model.Requests.Count);

        // Aprovado: sai pela tela de conversa do CRM, na linha do cliente, e ele ganha a etiqueta.
        var id = pending.GetProperty("id").GetString();
        var ok = await owner.PostAsJsonAsync($"/api/v1/neural/crm/replies/{id}/approve", new { message = "Oii Lucinea! Desculpa a demora 😊 Me manda seu CPF?" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var sent = Assert.Single(crm.Calls, c => c.Path == "/api/chat/send");
        Assert.Contains("5521983574865@c.us", sent.Body);
        Assert.Contains("Kamila_4", sent.Body);
        var tag = JsonDocument.Parse(Assert.Single(crm.Calls, c => c.Path == "/api/chat/tags/toggle").Body).RootElement;
        Assert.Equal(("5521983574865@c.us", "Kamila_4"), (tag.GetProperty("chatId").GetString(), tag.GetProperty("botId").GetString()));
    }

    [Fact]
    public async Task Perdido_que_a_atendente_ja_respondeu_nao_recebe_a_resposta_da_ia()
    {
        var (app, model, crm, owner, _) = await SetupAsync();
        using var _app = app;
        var tresDias = DateTime.UtcNow.AddDays(-3);
        var br = TimeZoneInfo.ConvertTimeFromUtc(tresDias, AssistantPrompt.BrazilTime);
        crm.Chats = [new { chat_id = "55@c.us", name = "ANA", last_message_at = tresDias.ToString("O"), canais = new[] { Canal("Kamila_4", br, false, "W1") } }];
        crm.History["55@c.us|Kamila_4"] = [Msg("W1", tresDias, false, "Oi, e a minha portabilidade?")];
        model.Decisions.Enqueue(Decide("responder", "Oi Ana! Desculpa a demora."));
        await owner.PostAsync("/api/v1/neural/crm/lost/scan", null);
        var id = (await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending")[0].GetProperty("id").GetString();

        // Enquanto a resposta esperava, a Kamila respondeu pelo CRM.
        crm.History["55@c.us|Kamila_4"] = [Msg("W1", tresDias, false, "Oi, e a minha portabilidade?"), Msg("K1", DateTime.UtcNow, true, "Oi Ana!", "Kamila Oliveira Borges")];
        var refused = await owner.PostAsJsonAsync($"/api/v1/neural/crm/replies/{id}/approve", new { message = "Oi Ana!" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Kamila Oliveira Borges já respondeu", await refused.Content.ReadAsStringAsync());
        Assert.DoesNotContain(crm.Calls, c => c.Path == "/api/chat/send");
        Assert.Empty((await owner.GetAsync("/api/v1/neural/crm").Json()).GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task Resposta_velha_nao_sai_quando_o_cliente_escreveu_de_novo()'''
rep('''
    [Fact]
    public async Task Resposta_velha_nao_sai_quando_o_cliente_escreveu_de_novo()''', test)
io.open(p,'w',encoding='utf-8',newline='').write(s)
