import io
p='src/server/Pontual.Server/Api/CrmEndpoints.cs'
s=io.open(p,encoding='utf-8').read()
def rep(a,b,count=1):
    global s
    assert s.count(a)==count,(a,s.count(a))
    s=s.replace(a,b)

# sondagem sai
a=s.index('    /// <summary>\n    /// SONDAGEM provisória')
b=s.index('    private async Task CleanupLoopAsync(CancellationToken ct)')
s=s[:a]+s[b:]
rep('        _ = ProbeLostAsync(stoppingToken);\n','')

rep('    private static async Task<string> LearnedAsync(','    public static async Task<string> LearnedAsync(')
rep('    private static async Task<(IReadOnlyList<string> Cached, string Found)> InstructionsAsync(','    public static async Task<(IReadOnlyList<string> Cached, string Found)> InstructionsAsync(')

# rotas
rep('''        api.MapGet("/neural/crm/users", UsersAsync).Panel(Access);''','''        api.MapGet("/neural/crm/users", UsersAsync).Panel(Access);
        api.MapPut("/neural/crm/lost", SaveLostAsync).Panel(Access);
        api.MapPost("/neural/crm/lost/scan", ScanLostAsync).Panel(Access);''')

rep('''    /// <summary>Os usuários do CRM, para escolher''','''    private sealed record LostBody(bool? Enabled);

    private static async Task<IResult> SaveLostAsync(HttpContext context, PontualDb db, AuditWriter audit, CancellationToken ct)
    {
        var admin = context.Admin();
        var enabled = (await context.ReadBodyAsync<LostBody>()).Enabled == true;
        await SetAsync(db, TextSetting.CrmLostEnabled, enabled ? "1" : "0", ct);
        await audit.SaveAsync(db, AuditInfo.By(admin, "crm.lost",
            $"{admin.Name} {(enabled ? "ligou" : "desligou")} a busca dos clientes perdidos do CRM"), cancellationToken: ct);
        return await OverviewAsync(context, db, ct);
    }

    /// <summary>Roda a busca agora (botão da tela), mesmo fora do horário e com a busca automática desligada.</summary>
    private static async Task<IResult> ScanLostAsync(IServiceScopeFactory scopes, TimeProvider time, ILogger<CrmLostWorker> logger, CancellationToken ct)
    {
        var result = await CrmLostWorker.RunAsync(scopes, time, logger, force: true, ct);
        return Results.Ok(new { result.Checked, result.Created, message = result.Note });
    }

    /// <summary>Os usuários do CRM, para escolher''')

# visão geral
rep('''            events,
            learned,''','''            events,
            learned,
            lostEnabled = settings.GetValueOrDefault(TextSetting.CrmLostEnabled, "") == "1",
            lostLastRun = settings.GetValueOrDefault(TextSetting.CrmLostLastRun, ""),''')
rep('reply = r.Reply, draft = r.Draft, attendant','reply = r.Reply, draft = r.Draft, kind = r.Kind, waitingSince = r.WaitingSince, attendant')

# aprovar: perdido confere no CRM e envia pela tela de conversa
rep('''        var now = time.GetUtcNow().UtcDateTime;
        await RefuseIfOutdatedAsync(db, queue, reply, now, ct);
        var access = await CrmClient.AccessAsync(db, ct) ?? throw ApiError.Conflict("A ligação com o CRM não está configurada. Preencha no topo desta tela.");

        reply.Reply = message;
        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = now;
        try
        {
            await crm.SendTextAsync(access, reply.ConversationId, message, ct);
        }''','''        var now = time.GetUtcNow().UtcDateTime;
        var lost = reply.Kind == CrmLost.Kind;
        if (!lost)
        {
            await RefuseIfOutdatedAsync(db, queue, reply, now, ct);
        }

        var access = await CrmClient.AccessAsync(db, ct) ?? throw ApiError.Conflict("A ligação com o CRM não está configurada. Preencha no topo desta tela.");
        if (lost)
        {
            string changed;
            try
            {
                changed = await CrmLost.ChangedAsync(crm, access, reply, ct);
            }
            catch (CrmFailure failure)
            {
                throw ApiError.Conflict($"Não deu para conferir a conversa no CRM. Nada foi enviado. {failure.Message}");
            }

            if (changed.Length > 0)
            {
                reply.Status = CrmReplyStatus.Outdated;
                reply.Error = changed;
                reply.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                throw ApiError.Conflict(changed);
            }
        }

        reply.Reply = message;
        reply.ReviewedByName = admin.Name;
        reply.UpdatedAt = now;
        try
        {
            if (lost)
            {
                await crm.SendChatAsync(access, reply.ChatId, reply.BotId, message, ct);
            }
            else
            {
                await crm.SendTextAsync(access, reply.ConversationId, message, ct);
            }
        }''')

# passar para atendente não vale para perdido
rep('''        var reply = await OpenAsync(db, id, ct);
        var message = AssistantEndpoints.Multiline(body.Message ?? "");''','''        var reply = await OpenAsync(db, id, ct);
        if (reply.Kind == CrmLost.Kind)
        {
            throw ApiError.Conflict("Este cliente já é da atendente dele no CRM. Para deixar com ela, use Recusar: nada vai ao cliente.");
        }

        var message = AssistantEndpoints.Multiline(body.Message ?? "");''')

io.open(p,'w',encoding='utf-8',newline='').write(s)

# etiqueta: perdido usa o chat e a linha guardados
s=io.open(p,encoding='utf-8').read()
a='''            var bodies = await db.CrmEvents.AsNoTracking().Where(e => e.ConversationId == reply.ConversationId)'''
assert s.count(a)==1
s=s.replace(a,'''            if (reply.Kind == CrmLost.Kind)
            {
                await crm.TagAsync(access, reply.ChatId, reply.BotId, ct);
                return "";
            }

            var bodies = await db.CrmEvents.AsNoTracking().Where(e => e.ConversationId == reply.ConversationId)''')
io.open(p,'w',encoding='utf-8',newline='').write(s)

p='src/server/Pontual.Server/Program.cs'
s=io.open(p,encoding='utf-8').read()
a='builder.Services.AddHostedService<CrmWorker>();'
assert s.count(a)==1
s=s.replace(a,a+'\nbuilder.Services.AddHostedService<Pontual.Server.Assistant.CrmLostWorker>();')
io.open(p,'w',encoding='utf-8',newline='').write(s)
