using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Assistant;
using Pontual.Server.Data;

namespace Pontual.Server.Rh;

/// <summary>O que uma atendente faz bem, como a análise da Central registrou. O nome vai junto (decisão do dono).</summary>
public sealed record RhStrengths(string Name, IReadOnlyList<string> Strengths, string Profile, DateTime? UpdatedAt);

public sealed record RhKnowledgeHit(Guid Id, string Kind, string Title, string Bank, int Part, int Parts, string Snippet);

public sealed record RhKnowledgeItem(Guid Id, string Kind, string Title, string Bank, string Content);

/// <summary>Um atendimento real disponível como caso. O <see cref="Id"/> é um código derivado: não é o número da conversa no CRM.</summary>
public sealed record RhCaseSummary(string Id, int Lines, int ClientLines, int AttendantLines, int Days);

/// <summary>O caso já sem dados pessoais (ver <see cref="Anonymizer"/>).</summary>
public sealed record RhCase(string Id, int Lines, string Text);

public sealed record RhStatus(int Attendants, int Strengths, int Knowledge, int Cases);

/// <summary>
/// A porta entre o Agente RH e a Central Neural (a linha "api" do mapa). O agente só enxerga a Central por aqui:
/// os pontos fortes de cada atendente, o conhecimento da base e os atendimentos reais, estes sempre anônimos.
/// É só leitura e não chama a IA.
/// </summary>
public interface IRhCentral
{
    Task<RhStatus> StatusAsync(CancellationToken ct);
    Task<IReadOnlyList<RhStrengths>> StrengthsAsync(CancellationToken ct);
    Task<IReadOnlyList<RhKnowledgeHit>> SearchKnowledgeAsync(string query, int max, CancellationToken ct);
    Task<RhKnowledgeItem?> KnowledgeAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RhCaseSummary>> CasesAsync(int max, CancellationToken ct);
    Task<RhCase?> CaseAsync(string id, CancellationToken ct);
}

public sealed class RhCentral(PontualDb db) : IRhCentral
{
    /// <summary>Uma conversa só vira caso se o cliente e a equipe escreveram pelo menos isto cada um.</summary>
    public const int MinLinesEach = 2;

    private const int MaxLines = 150;
    private const int MaxLineChars = 1500;
    private const int SnippetChars = 240;

    public async Task<RhStatus> StatusAsync(CancellationToken ct)
    {
        var strengths = await StrengthsAsync(ct);
        return new RhStatus(
            strengths.Count,
            strengths.Sum(s => s.Strengths.Count),
            await Knowledge().CountAsync(ct),
            (await ConversationsAsync(ct)).Count);
    }

    public async Task<IReadOnlyList<RhStrengths>> StrengthsAsync(CancellationToken ct)
    {
        var settings = await db.TextSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var sources = await IdealAttendant.SourcesAsync(db, settings, ct);
        return [.. sources.Select(s => new RhStrengths(s.Name, s.Strengths, s.Text, s.UpdatedAt))];
    }

    // O que é "só para atendentes" na base (logins, senhas) fica de fora: o agente escreve material que candidatos vão ler.
    private IQueryable<AssistantScript> Knowledge() =>
        db.AssistantScripts.AsNoTracking().Where(s => s.Enabled && !s.StaffOnly && s.Content != "");

    public async Task<IReadOnlyList<RhKnowledgeHit>> SearchKnowledgeAsync(string query, int max, CancellationToken ct)
    {
        if (query.Trim().Length < 2)
        {
            return [];
        }

        var items = await Knowledge().ToListAsync(ct);
        return [.. KnowledgeText.Search(items, query, Math.Clamp(max, 1, 40), SnippetChars)
            .Select(h => new RhKnowledgeHit(h.Item.Id, h.Item.Kind, h.Item.Title, h.Item.Bank, h.Part, h.Parts, h.Snippet))];
    }

    public async Task<RhKnowledgeItem?> KnowledgeAsync(Guid id, CancellationToken ct)
    {
        var item = await Knowledge().FirstOrDefaultAsync(s => s.Id == id, ct);
        return item is null ? null : new RhKnowledgeItem(item.Id, item.Kind, item.Title, item.Bank, item.Content);
    }

    private sealed record Conversation(string Id, int Client, int Team, DateTime First, DateTime Last);

    /// <summary>As conversas do CRM que servem de caso, da mais recente para a mais antiga.</summary>
    private async Task<List<Conversation>> ConversationsAsync(CancellationToken ct)
    {
        var all = await db.CrmEvents.AsNoTracking()
            .Where(e => e.ConversationId != "" && e.Text != "")
            .GroupBy(e => e.ConversationId)
            .Select(g => new
            {
                Id = g.Key,
                Client = g.Sum(e => e.Author == CrmAuthor.Client ? 1 : 0),
                Team = g.Sum(e => e.Author == CrmAuthor.Team ? 1 : 0),
                First = g.Min(e => e.At),
                Last = g.Max(e => e.At),
            })
            .ToListAsync(ct);
        return [.. all.Where(c => c.Client >= MinLinesEach && c.Team >= MinLinesEach)
            .OrderByDescending(c => c.Last)
            .Select(c => new Conversation(c.Id, c.Client, c.Team, c.First, c.Last))];
    }

    /// <summary>O código do caso: derivado do número da conversa, mas sem dar para voltar a ele.</summary>
    private static string CaseId(string conversationId) =>
        "caso-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("rh|" + conversationId)))[..12].ToLowerInvariant();

    public async Task<IReadOnlyList<RhCaseSummary>> CasesAsync(int max, CancellationToken ct)
    {
        var recent = (await ConversationsAsync(ct)).Take(Math.Clamp(max, 1, 200));
        return [.. recent.Select(c => new RhCaseSummary(
            CaseId(c.Id), c.Client + c.Team, c.Client, c.Team, (Local(c.Last).Date - Local(c.First).Date).Days + 1))];
    }

    public async Task<RhCase?> CaseAsync(string id, CancellationToken ct)
    {
        var conversationId = (await ConversationsAsync(ct)).Select(c => c.Id).FirstOrDefault(c => CaseId(c) == id);
        if (conversationId is null)
        {
            return null;
        }

        var rows = await db.CrmEvents.AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.Text != "")
            .OrderBy(e => e.Id)
            .Select(e => new { e.At, e.Author, e.Text, e.Contact, e.Sender })
            .Take(MaxLines)
            .ToListAsync(ct);

        // Os nomes que a Central conhece saem do texto: o contato no CRM, quem digitou e as atendentes cadastradas.
        var clients = rows.Select(r => r.Contact).Where(n => n.Length > 0).Distinct().ToList();
        var staff = rows.Select(r => r.Sender).Where(n => n.Length > 0).Distinct()
            .Concat(await db.Devices.AsNoTracking().Where(d => d.AttendantName != null && d.AttendantName != "").Select(d => d.AttendantName!).ToListAsync(ct))
            .Concat(await db.Admins.AsNoTracking().Select(a => a.Name).ToListAsync(ct))
            .ToList();

        var text = new StringBuilder();
        var firstDay = Local(rows[0].At).Date;
        foreach (var row in rows)
        {
            // Dia contado a partir do começo da conversa, sem a data real.
            var local = Local(row.At);
            var said = row.Text.Length > MaxLineChars ? row.Text[..MaxLineChars] + " […]" : row.Text;
            text.Append("[dia ").Append((local.Date - firstDay).Days + 1).Append(' ').Append(local.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("] ")
                .Append(row.Author == CrmAuthor.Client ? "Cliente" : "Atendente").Append(": ")
                .Append(Anonymizer.Clean(said, clients, staff)).Append('\n');
        }

        return new RhCase(id, rows.Count, text.ToString().TrimEnd());
    }

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), AssistantPrompt.BrazilTime);
}
