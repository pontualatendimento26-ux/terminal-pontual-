using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>
/// Textos que a atendente salva no perfil dela com uma palavra-chave. Quem salva e quem responde é o servidor,
/// sem chamar a IA: o texto volta igual ao que ela mandou e não gasta saldo. Não é conhecimento da assistente;
/// cada atendente só usa os seus.
/// O cadastro em andamento não tem estado próprio: é relido da conversa atual, pelas perguntas fixas abaixo.
/// </summary>
public static class SavedTexts
{
    public const int MaxKeyword = 60;
    public const int MaxPerDevice = 200;

    public const string AskText = "Certo, vou salvar no seu perfil. Mande agora o texto que você quer guardar. (Para desistir, escreva cancelar.)";
    public const string AskKeyword = "Recebi o texto. Agora mande a palavra-chave que vai chamar esse texto. (Para desistir, escreva cancelar.)";
    public const string BadKeyword = "Essa palavra-chave não serve: use uma linha só, com até 60 caracteres, com letras ou números, e que não seja um comando. Mande outra. (Para desistir, escreva cancelar.)";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(500);

    private static readonly Regex Trigger = new(
        @"^\s*(?:(?:oi|ol[aá]|por favor)[\s,]+)?(?:(?:eu\s+)?(?:quero|queria|gostaria|preciso|pode|poderia)\s+(?:que\s+)?(?:(?:voc[eê]|vc)\s+)?)?" +
        @"(?:salv(?:a|e|ar)|guard(?:a|e|ar)|grav(?:a|e|ar))\s+(?:(?:isso|isto|(?:(?:esse|este|essa|esta|o|a|um|uma)\s+)?(?:texto|mensagem))\s+)?(?:aqui\s+)?" +
        @"(?:no|em)\s+meu\s+perfil(?:\s+por favor)?[ \t]*(?:$|[\n:.!?,-]+)", Options, Limit);

    private static readonly Regex KeywordLine = new(
        @"^[ \t]*(?:e[ \t]+)?(?:a[ \t]+)?palavra[ \t-]*chave[ \t]*(?:é[ \t]|e[ \t]|:|=|-)[ \t]*:?[ \t]*(?<key>[^\n]+)$", Options | RegexOptions.Multiline, Limit);

    private static readonly Regex TextLabel = new(@"^\s*texto(?: de exemplo)?[ \t]*:\s*", Options, Limit);

    private static readonly Regex DeleteCommand = new(
        @"^\s*(?:apagar?|apague|excluir|exclua|remover|remova)\s+(?:a\s+)?palavra[\s-]*chave\s*:?\s*(?<key>[^\n]+)$", Options, Limit);

    private static readonly string[] ListCommands =
        ["minhas palavras-chave", "minhas palavras chave", "minhas palavras chaves", "meus textos salvos"];

    /// <summary>A forma de comparar: sem acento, sem maiúscula, sem pontuação nas pontas e com espaços simples.</summary>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        var words = builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words).Trim(' ', '.', ',', ';', ':', '!', '?', '"', '\'', '“', '”', '«', '»');
    }

    /// <summary>
    /// A resposta pronta para o que a atendente escreveu, ou nulo se a pergunta é para a IA.
    /// <paramref name="history"/> são as falas anteriores da conversa atual, da mais antiga para a mais nova.
    /// </summary>
    public static async Task<string?> ReplyAsync(
        PontualDb db, Guid deviceId, IReadOnlyList<AssistantMessage> history, string message, DateTime now, CancellationToken ct)
    {
        var typed = Normalize(message);
        var inputs = Pending(history);
        // Sem cadastro em andamento, "cancelar" é uma fala comum.
        if (inputs.Count > 0 && typed is ("cancelar" or "cancela"))
        {
            return "Tudo bem, não salvei nada.";
        }

        if (Trigger.IsMatch(message))
        {
            inputs = [];
        }
        else if (inputs.Count == 0)
        {
            return await CommandAsync(db, deviceId, message, typed, ct);
        }

        inputs.Add(message);
        var (text, keyword, bad) = Collect(inputs);
        if (text.Length == 0)
        {
            return AskText;
        }

        if (keyword.Length == 0)
        {
            return bad ? BadKeyword : AskKeyword;
        }

        var normalized = Normalize(keyword);
        var saved = await db.SavedTexts.FirstOrDefaultAsync(s => s.DeviceId == deviceId && s.Keyword == normalized, ct);
        var replaced = saved is not null;
        if (saved is null)
        {
            if (await db.SavedTexts.CountAsync(s => s.DeviceId == deviceId, ct) >= MaxPerDevice)
            {
                return $"Você já tem {MaxPerDevice} textos salvos. Apague um antes (apagar palavra-chave e o nome dela) e tente de novo.";
            }

            saved = new SavedText { DeviceId = deviceId, Keyword = normalized };
            db.SavedTexts.Add(saved);
        }

        saved.Label = keyword;
        saved.Text = text;
        saved.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (replaced ? "Pronto, troquei o texto que já existia com essa palavra-chave." : "Pronto, salvei no seu perfil.")
            + $" Quando você mandar só \"{keyword}\", eu respondo com este texto:\n\n{text}";
    }

    /// <summary>O que a atendente já mandou no cadastro em andamento; vazio se não há cadastro.</summary>
    private static List<string> Pending(IReadOnlyList<AssistantMessage> history)
    {
        var inputs = new List<string>();
        for (var i = history.Count - 1; i > 0; i -= 2)
        {
            if (history[i].Role != AssistantRole.Assistant || history[i].Text is not (AskText or AskKeyword or BadKeyword)
                || history[i - 1].Role != AssistantRole.User)
            {
                break;
            }

            inputs.Insert(0, history[i - 1].Text);
            if (Trigger.IsMatch(history[i - 1].Text))
            {
                return inputs;
            }
        }

        return [];
    }

    private static (string Text, string Keyword, bool Bad) Collect(List<string> inputs)
    {
        string text = "", keyword = "";
        var bad = false;
        for (var i = 0; i < inputs.Count; i++)
        {
            var rest = i == 0 ? Trigger.Replace(inputs[i], "", 1) : inputs[i];
            string? candidate = null;
            var line = KeywordLine.Matches(rest).LastOrDefault();
            if (line is not null)
            {
                candidate = line.Groups["key"].Value;
                rest = rest.Remove(line.Index, line.Length);
            }

            rest = TextLabel.Replace(rest, "", 1).Trim();
            if (text.Length == 0)
            {
                text = rest;
            }
            else if (candidate is null && rest.Length > 0)
            {
                candidate = rest;
            }

            if (candidate is not null)
            {
                candidate = candidate.Trim().Trim('"', '“', '”');
                bad = !Valid(candidate);
                keyword = bad ? "" : candidate;
            }
        }

        return (text, keyword, bad);
    }

    private static bool Valid(string keyword)
    {
        var normalized = Normalize(keyword);
        return keyword.Length <= MaxKeyword && !keyword.Contains('\n') && normalized.Any(char.IsLetterOrDigit)
            && normalized is not ("cancelar" or "cancela") && !ListCommands.Contains(normalized)
            && !Trigger.IsMatch(keyword) && !DeleteCommand.IsMatch(keyword);
    }

    private static async Task<string?> CommandAsync(PontualDb db, Guid deviceId, string message, string typed, CancellationToken ct)
    {
        if (ListCommands.Contains(typed))
        {
            var labels = await db.SavedTexts.AsNoTracking().Where(s => s.DeviceId == deviceId)
                .OrderBy(s => s.Keyword).Select(s => s.Label).ToListAsync(ct);
            return labels.Count == 0
                ? "Você ainda não tem textos salvos. Para salvar, escreva: quero que salve isso no meu perfil."
                : "Suas palavras-chave:\n" + string.Join('\n', labels.Select(l => "- " + l));
        }

        if (DeleteCommand.Match(message) is { Success: true } delete)
        {
            var label = delete.Groups["key"].Value.Trim();
            var key = Normalize(label);
            var removed = await db.SavedTexts.Where(s => s.DeviceId == deviceId && s.Keyword == key).ExecuteDeleteAsync(ct);
            return removed > 0
                ? $"Pronto, apaguei a palavra-chave \"{label}\" do seu perfil."
                : $"Não achei a palavra-chave \"{label}\" no seu perfil. Para ver a lista, escreva: minhas palavras-chave.";
        }

        return typed.Length is 0 or > MaxKeyword ? null
            : await db.SavedTexts.AsNoTracking().Where(s => s.DeviceId == deviceId && s.Keyword == typed)
                .Select(s => s.Text).FirstOrDefaultAsync(ct);
    }
}
