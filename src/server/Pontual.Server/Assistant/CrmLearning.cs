using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>
/// O que cada atendente escreveu aos clientes pelo CRM (sistema interno), para a análise do perfil.
/// O CRM avisa cada mensagem da equipe com o nome de quem digitou (remetente.nome, o mesmo nome que aparece em
/// "Responsável"). A mensagem é da atendente cujo nome na Central começa esse nome: "Kamila" na Central é
/// "Kamila Oliveira Borges" no CRM. Mensagem mandada pelo celular chega como "Externo"; aí vale a linha de
/// WhatsApp ligada à atendente na Central.
/// Os avisos não são apagados depois da análise: a tela Clientes do CRM também os usa, e eles saem em 30 dias.
/// </summary>
public static class CrmLearning
{
    /// <summary>Nome que o CRM dá a quem respondeu pelo celular, fora do CRM.</summary>
    private const string OffSite = "externo";

    /// <summary>Quantas mensagens da atendente entram em um lote.</summary>
    private const int MaxMessages = 200;
    /// <summary>Falas anteriores da conversa que vão junto, para a IA entender do que se trata.</summary>
    private const int ContextLines = 6;
    private const int MaxLines = 80;

    /// <summary>Uma mensagem que a atendente escreveu no CRM e que a análise ainda não leu.</summary>
    public sealed record Message(long Id, DateTime At, string ConversationId);

    /// <summary>As conversas prontas para a IA e até qual aviso elas vão.</summary>
    public sealed record Batch(string Text, int Conversations, int Messages, long Through, bool More);

    /// <summary>De cada máquina, as mensagens que a atendente escreveu no CRM e que ainda não passaram pela análise.</summary>
    public static async Task<Dictionary<Guid, List<Message>>> PendingAsync(PontualDb db, CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<Message>>();
        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.Approved && d.AttendantName != null && d.AttendantName != "")
            .Select(d => new { d.Id, d.AttendantName, d.Windows, d.CreatedAt })
            .ToListAsync(ct);

        // Quem tem computador e celular com o mesmo nome fica com o perfil no computador.
        var people = devices
            .OrderBy(d => (d.Windows ?? "").TrimStart().StartsWith("android", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.CreatedAt)
            .Select(d => (d.Id, Name: CrmAgent.Fold(d.AttendantName!)))
            .Where(p => p.Name.Length > 0)
            .ToList();
        if (people.Count == 0)
        {
            return result;
        }

        var known = people.Select(p => p.Id).ToHashSet();
        var lines = (await db.AssistantProfiles.AsNoTracking().Where(p => p.CrmLines != "")
            .Select(p => new { p.DeviceId, p.CrmLines }).ToListAsync(ct))
            .Where(l => known.Contains(l.DeviceId)).ToList();
        var through = await db.AttendantInsights.AsNoTracking().ToDictionaryAsync(i => i.DeviceId, i => i.CrmAnalyzedThroughEventId, ct);
        var from = people.Min(p => through.GetValueOrDefault(p.Id));

        // O corpo do aviso só é lido nos avisos antigos, de antes de o nome de quem digitou ser guardado à parte.
        var events = await db.CrmEvents.AsNoTracking()
            .Where(e => e.Author == CrmAuthor.Team && e.Id > from && e.ConversationId != "" && e.Text != "")
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.At, e.ConversationId, e.Sender, e.Line, Body = e.Sender == "" ? e.Body : "" })
            .ToListAsync(ct);

        var owners = new Dictionary<string, Guid?>();
        foreach (var e in events)
        {
            var sender = CrmAgent.Fold(e.Sender.Length > 0 ? e.Sender : CrmAgent.Parse(e.Body).Sender);
            if (sender.Length == 0)
            {
                continue;
            }

            Guid? owner;
            if (sender == OffSite)
            {
                owner = lines.FirstOrDefault(l => CrmAgent.HasLine(l.CrmLines, e.Line))?.DeviceId;
            }
            else if (!owners.TryGetValue(sender, out owner))
            {
                owners[sender] = owner = OwnerOf(sender, people);
            }

            if (owner is not { } deviceId || e.Id <= through.GetValueOrDefault(deviceId))
            {
                continue;
            }

            if (!result.TryGetValue(deviceId, out var list))
            {
                result[deviceId] = list = [];
            }

            list.Add(new Message(e.Id, e.At, e.ConversationId));
        }

        return result;
    }

    /// <summary>A atendente cujo nome na Central é o começo do nome no CRM; entre duas, a de nome mais completo.</summary>
    private static Guid? OwnerOf(string sender, List<(Guid Id, string Name)> people)
    {
        Guid? best = null;
        var length = 0;
        foreach (var (id, name) in people)
        {
            if (name.Length > length && (sender == name || sender.StartsWith(name + " ", StringComparison.Ordinal)))
            {
                best = id;
                length = name.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Monta as conversas em que a atendente escreveu: as falas novas (dela, do cliente e de quem mais escreveu)
    /// e algumas anteriores para dar contexto. Para de acrescentar conversas quando o texto passa de <paramref name="budget"/>.
    /// </summary>
    public static async Task<Batch> BuildAsync(PontualDb db, IReadOnlyList<Message> pending, long through, int budget, CancellationToken ct)
    {
        var more = pending.Count > MaxMessages;
        var mine = pending.OrderBy(m => m.Id).Take(MaxMessages).ToList();
        var upTo = mine[^1].Id;
        var mineIds = mine.Select(m => m.Id).ToHashSet();
        var conversationIds = mine.Select(m => m.ConversationId).Distinct().ToList();

        var rows = await db.CrmEvents.AsNoTracking()
            .Where(e => conversationIds.Contains(e.ConversationId) && e.Id <= upTo && e.Text != "")
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.At, e.ConversationId, e.Author, e.Text })
            .ToListAsync(ct);
        var byConversation = rows.ToLookup(r => r.ConversationId);

        var text = new StringBuilder();
        var conversations = 0;
        foreach (var id in conversationIds)
        {
            var all = byConversation[id].ToList();
            var first = Math.Max(0, all.FindIndex(r => r.Id > through));
            var shown = all.Skip(Math.Max(0, first - ContextLines)).ToList();
            if (shown.Count > MaxLines)
            {
                shown = shown[^MaxLines..];
            }

            var block = new StringBuilder().Append("<conversa numero=\"").Append(conversations + 1).Append("\">\n");
            foreach (var row in shown)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(row.At, AssistantPrompt.BrazilTime);
                block.Append('[').Append(local.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)).Append("] ")
                    .Append(row.Author == CrmAuthor.Client ? "Cliente" : mineIds.Contains(row.Id) ? "Atendente" : "Pontual")
                    .Append(": ").Append(row.Text).Append('\n');
            }

            block.Append("</conversa>\n");
            if (conversations > 0 && text.Length + block.Length > budget)
            {
                // O que ficou de fora volta no próximo lote, a partir da primeira mensagem dela nesta conversa.
                upTo = mine.First(m => m.ConversationId == id).Id - 1;
                more = true;
                break;
            }

            text.Append(block);
            conversations++;
        }

        return new Batch(text.ToString(), conversations, mine.Count(m => m.Id <= upTo), upTo, more);
    }
}
