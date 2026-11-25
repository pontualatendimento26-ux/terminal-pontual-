p='Api/CrmEndpoints.cs'
s=open(p,encoding='utf-8').read()
def rep(a,b):
    global s
    assert s.count(a)==1,a
    s=s.replace(a,b)
rep('''            .Select(e => new { id = e.Id, at = e.At, conversationId = e.ConversationId, contact = e.Contact, phone = e.Phone, author = e.Author, text = e.Text, body = e.Body })
            .ToListAsync(ct);''','''            .Select(e => new { id = e.Id, at = e.At, conversationId = e.ConversationId, contact = e.Contact, phone = e.Phone, author = e.Author, text = e.Text, body = e.Body })
            .ToListAsync(ct);
        // O que a IA já aprendeu: respostas aprovadas e enviadas, por atendente, e quantas foram corrigidas antes.
        var learned = (await db.CrmReplies.AsNoTracking()
                .Where(r => r.Status == CrmReplyStatus.Sent && r.ReviewedByName != "" && r.Reply != "")
                .Select(r => new { r.Attendant, r.Draft, r.Reply }).ToListAsync(ct))
            .GroupBy(r => r.Attendant)
            .Select(g => new { attendant = g.Key, approved = g.Count(), edited = g.Count(r => r.Draft.Length > 0 && r.Draft.Trim() != r.Reply.Trim()) })
            .OrderByDescending(g => g.approved).ToList();''')
rep('''            events,
        });''','''            events,
            learned,
        });''')
rep('reply = r.Reply, attendant','reply = r.Reply, draft = r.Draft, attendant')
rep('''await db.CrmReplies.Where(r => r.UpdatedAt < old).ExecuteDeleteAsync(ct);''','''// As aprovadas ficam um ano: são os exemplos com que a IA aprende.
                await db.CrmReplies.Where(r => r.UpdatedAt < old && r.Status != CrmReplyStatus.Sent).ExecuteDeleteAsync(ct);
                var year = time.GetUtcNow().UtcDateTime.AddDays(-365);
                await db.CrmReplies.Where(r => r.UpdatedAt < year).ExecuteDeleteAsync(ct);''')
open(p,'w',encoding='utf-8',newline='').write(s)
