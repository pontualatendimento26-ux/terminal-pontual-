import io
def edit(p, pairs):
    s=io.open(p,encoding='utf-8').read()
    for a,b in pairs:
        assert s.count(a)==1,(p,a,s.count(a))
        s=s.replace(a,b)
    io.open(p,'w',encoding='utf-8',newline='').write(s)

edit('Assistant/CrmLost.cs',[
('''    public static async Task<Result> ScanAsync(IServiceProvider services, bool force, CancellationToken ct)''',
'''    /// <summary>Até quantos dias para trás a tela deixa escolher.</summary>
    public const int MaxDays = 60;
    /// <summary>Com datas escolhidas na tela, quem escreveu há menos que isso ainda fica de fora: a atendente pode estar respondendo.</summary>
    public static readonly TimeSpan MinWaitChosen = TimeSpan.FromHours(1);

    /// <summary>
    /// Procura os clientes perdidos e escreve as respostas. <paramref name="force"/> roda mesmo com a busca desligada (botão da tela).
    /// Sem datas: de 15 dias atrás até 24 horas atrás. Com datas (dias de Brasília, inclusive): o período escolhido, até 1 hora atrás.
    /// </summary>
    public static async Task<Result> ScanAsync(IServiceProvider services, bool force, CancellationToken ct, DateTime? fromDay = null, DateTime? toDay = null)'''),
('''        var oldest = now - Window;
        var newest = now - MinWait;
        var today = TimeZoneInfo.ConvertTimeFromUtc(now, AssistantPrompt.BrazilTime).Date;''',
'''        var today = TimeZoneInfo.ConvertTimeFromUtc(now, AssistantPrompt.BrazilTime).Date;
        var chosen = fromDay is not null || toDay is not null;
        var firstDay = (fromDay ?? today - Window).Date;
        var lastDay = (toDay ?? today).Date;
        var oldest = chosen ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(firstDay, DateTimeKind.Unspecified), AssistantPrompt.BrazilTime) : now - Window;
        var newest = chosen
            ? new[] { TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(lastDay.AddDays(1), DateTimeKind.Unspecified), AssistantPrompt.BrazilTime), now - MinWaitChosen }.Min()
            : now - MinWait;'''),
('''Where(r => r.Kind == Kind && r.CreatedAt >= oldest)''','''Where(r => r.Kind == Kind && (r.WaitingSince == null || r.WaitingSince >= oldest))'''),
('''WaitingChatsAsync(access, lines, today - Window, today, page, ct)''','''WaitingChatsAsync(access, lines, firstDay, lastDay, page, ct)'''),
('''                if (Time(Str(chat, "last_message_at"), utc: true) is { } lastAt && lastAt < oldest)''','''                var lastAt = Time(Str(chat, "last_message_at"), utc: true);
                if (lastAt is { } stale && stale < oldest)'''),
('''            : checkedCount > 0 ? "Nenhum cliente perdido novo." : "Nenhuma conversa sem resposta entre 1 e 15 dias no funil.";''',
'''            : checkedCount > 0 ? "Nenhum cliente perdido novo." : chosen
                ? $"Nenhuma conversa sem resposta entre {firstDay:dd/MM} e {lastDay:dd/MM} no funil."
                : "Nenhuma conversa sem resposta entre 1 e 15 dias no funil.";
        if (chosen)
        {
            note = $"De {firstDay:dd/MM} a {lastDay:dd/MM}: {note}";
        }'''),
('''    public static async Task<CrmLost.Result> RunAsync(IServiceScopeFactory scopes, TimeProvider time, ILogger logger, bool force, CancellationToken ct)''',
'''    public static async Task<CrmLost.Result> RunAsync(IServiceScopeFactory scopes, TimeProvider time, ILogger logger, bool force, CancellationToken ct,
        DateTime? fromDay = null, DateTime? toDay = null)'''),
('''                result = await CrmLost.ScanAsync(scope.ServiceProvider, force, ct);''','''                result = await CrmLost.ScanAsync(scope.ServiceProvider, force, ct, fromDay, toDay);'''),
])

edit('Api/CrmEndpoints.cs',[
('''    private static async Task<IResult> ScanLostAsync(IServiceScopeFactory scopes, TimeProvider time, ILogger<CrmLostWorker> logger, CancellationToken ct)
    {
        var result = await CrmLostWorker.RunAsync(scopes, time, logger, force: true, ct);''',
'''    private sealed record ScanBody(string? From, string? To);

    private static async Task<IResult> ScanLostAsync(HttpContext context, IServiceScopeFactory scopes, TimeProvider time, ILogger<CrmLostWorker> logger, CancellationToken ct)
    {
        var body = context.Request.ContentLength > 0 ? await context.ReadBodyAsync<ScanBody>() : new ScanBody(null, null);
        static DateTime? Day(string? text, string label) =>
            string.IsNullOrWhiteSpace(text) ? null
            : DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d
            : throw ApiError.BadRequest($"A data {label} não é válida.");
        var from = Day(body.From, "de início");
        var to = Day(body.To, "de fim");
        var today = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, AssistantPrompt.BrazilTime).Date;
        if (from > to)
        {
            throw ApiError.BadRequest("A data de início precisa ser antes da data de fim.");
        }

        if (from > today || to > today)
        {
            throw ApiError.BadRequest("Escolha datas até hoje.");
        }

        if ((from ?? to) is { } first && first < today.AddDays(-CrmLost.MaxDays))
        {
            throw ApiError.BadRequest($"A busca olha no máximo {CrmLost.MaxDays} dias para trás.");
        }

        var result = await CrmLostWorker.RunAsync(scopes, time, logger, force: true, ct, from, to);'''),
])
