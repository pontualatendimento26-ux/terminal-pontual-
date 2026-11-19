using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>Monta as instruções que a IA recebe: as comuns a todas as atendentes e as de cada uma.</summary>
public static class AssistantPrompt
{
    private const string Rules =
        """
        Você é a assistente interna da Pontual Consig, um correspondente bancário de crédito consignado. Quem conversa com você é uma atendente da Pontual, durante o expediente, enquanto opera os portais dos bancos e promotoras para os clientes. Ela recorre a você para fazer uma operação do jeito certo: o passo a passo de digitação de uma proposta, as regras de cada banco, os documentos exigidos, os prazos, e o que fazer quando algo dá errado.

        Sua fonte é a base de conhecimento da Pontual que aparece mais abaixo: roteiros operacionais, documentos e conhecimentos escritos ou aprovados pela própria Pontual. Ela vale mais do que o seu conhecimento geral, porque descreve como a Pontual trabalha com cada banco hoje.

        Como responder:
        - Quando a base cobre a pergunta, siga-a fielmente, na ordem dos passos, e diga de qual roteiro ou documento veio a informação.
        - Quando nada na base cobre a pergunta, diga isso com clareza e oriente a atendente a confirmar com a supervisão. Você pode acrescentar o que sabe sobre consignado em geral, desde que deixe claro que é conhecimento geral e não regra do banco. Taxas, limites, prazos e tabelas mudam com frequência, e um número errado vira proposta recusada ou prejuízo para o cliente; por isso nunca apresente um valor que não está na base como se fosse regra.
        - Se dois conteúdos da base se contradizem, ou a pergunta pode se referir a mais de um banco, pergunte qual é o caso antes de responder.
        - A atendente costuma estar no meio de um atendimento. Vá direto ao ponto, em português simples. Para procedimentos, use passos numerados curtos. Escreva em texto simples: sem tabelas e sem títulos; **negrito** pode ser usado para destacar o que não pode ser esquecido.
        - Você não precisa de dados pessoais de clientes (CPF, número de benefício, senha, conta) para explicar um procedimento. Se a atendente colar esse tipo de dado, responda à dúvida sem repeti-lo e lembre, em uma frase, que não é preciso informá-lo aqui.
        - Você é uma ferramenta de trabalho. Dúvidas rápidas de redação, contas ou uso do computador podem ser atendidas; para o resto, diga com gentileza que está aqui para ajudar no atendimento.
        """;

    /// <summary>
    /// Parte igual para todo mundo. A ordem dos roteiros é fixa (título, depois id) para o texto sair
    /// idêntico a cada pergunta: é isso que permite ao serviço da IA reaproveitar o cache.
    /// </summary>
    public static string Shared(string generalInstructions, IEnumerable<AssistantScript> scripts)
    {
        var text = Opening(generalInstructions);
        var ordered = KnowledgeText.Ordered(scripts);
        if (ordered.Count == 0)
        {
            text.Append("\n\nAinda não há nenhum conhecimento cadastrado na base. Avise a atendente disso quando ela perguntar sobre um procedimento de banco, e responda apenas com conhecimento geral, marcado como tal.");
            return text.ToString();
        }

        text.Append("\n\nBase de conhecimento da Pontual:\n<base>");
        foreach (var script in ordered)
        {
            text.Append('\n').Append(Render(script, script.Content, null));
        }

        text.Append("\n</base>");
        return text.ToString();
    }

    /// <summary>
    /// Modo índice, para quando a base não cabe inteira: a IA recebe a lista numerada dos conhecimentos e abre
    /// o texto pelas ferramentas de <see cref="KnowledgeTools"/>. <paramref name="ordered"/> é a mesma lista dada a elas.
    /// </summary>
    public static string Index(string generalInstructions, IReadOnlyList<AssistantScript> ordered)
    {
        var text = Opening(generalInstructions);
        text.Append(
            """


            A base de conhecimento da Pontual é grande demais para caber inteira aqui. Abaixo está só o índice: número, tipo, título e um resumo de cada conhecimento. O texto de verdade você lê com as ferramentas:
            - abrir_conhecimento: abre um conhecimento pelo número (e pela parte, quando ele tem mais de uma).
            - buscar_na_base: procura palavras em toda a base, quando o índice não deixa claro onde está a resposta.

            Abra o que a pergunta envolve antes de responder, de preferência tudo na mesma rodada. O índice nunca basta para afirmar uma regra, um prazo ou um passo: ele diz onde a informação está, não qual ela é. Se a conversa já trouxe o conteúdo de que você precisa, não abra de novo. Para um cumprimento ou uma dúvida que não depende da base, responda direto.

            <indice>
            """);

        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var parts = KnowledgeText.Parts(item.Content).Count;
            text.Append('\n').Append(i + 1).Append(". [").Append(KnowledgeKind.Label(item.Kind)).Append("] ").Append(item.Title);
            if (item.Bank.Length > 0)
            {
                text.Append(" | banco ou assunto: ").Append(item.Bank);
            }

            if (item.Category.Length > 0)
            {
                text.Append(" | categoria: ").Append(item.Category);
            }

            if (parts > 1)
            {
                text.Append(" | ").Append(parts).Append(" partes");
            }

            var summary = item.Description.Length > 0 ? item.Description : FirstLine(item.Content);
            text.Append("\n   ").Append(summary.Length > 240 ? summary[..240] + "…" : summary);
            if (item.Keywords.Length > 0)
            {
                text.Append("\n   palavras-chave: ").Append(item.Keywords);
            }
        }

        text.Append("\n</indice>");
        return text.ToString();
    }

    /// <summary>Um conhecimento do jeito que a IA o lê, inteiro ou uma parte dele.</summary>
    public static string Render(AssistantScript item, string content, string? part)
    {
        var tag = item.Kind switch
        {
            KnowledgeKind.Document => "documento",
            KnowledgeKind.Knowledge => "conhecimento",
            _ => "roteiro",
        };

        var text = new StringBuilder().Append('<').Append(tag).Append(" titulo=\"").Append(Attribute(item.Title)).Append('"');
        if (item.Bank.Length > 0)
        {
            text.Append(" banco=\"").Append(Attribute(item.Bank)).Append('"');
        }

        if (item.Category.Length > 0)
        {
            text.Append(" categoria=\"").Append(Attribute(item.Category)).Append('"');
        }

        if (part is not null)
        {
            text.Append(" trecho=\"").Append(part).Append('"');
        }

        text.Append(">\n");
        if (item.AiInstructions.Length > 0)
        {
            text.Append("Como usar este conteúdo: ").Append(item.AiInstructions).Append("\n\n");
        }

        return text.Append(content).Append("\n</").Append(tag).Append('>').ToString();
    }

    private static StringBuilder Opening(string generalInstructions)
    {
        var text = new StringBuilder(Rules);
        if (generalInstructions.Length > 0)
        {
            text.Append("\n\nOrientações gerais da Pontual para você:\n<orientacoes_gerais>\n")
                .Append(generalInstructions)
                .Append("\n</orientacoes_gerais>");
        }

        return text;
    }

    private static string Attribute(string value) => value.Replace('"', '\'').Replace('\n', ' ');

    private static string FirstLine(string content)
    {
        var end = content.IndexOf('\n');
        return (end < 0 ? content : content[..end]).Trim();
    }

    /// <summary>Perfil de quem pergunta. Fica depois da parte comum, fora do cache compartilhado.</summary>
    public static string Personal(Device device, AssistantProfile? profile, DateTime utcNow)
    {
        var name = string.IsNullOrWhiteSpace(device.AttendantName) ? "uma atendente" : device.AttendantName;
        var today = TimeZoneInfo.ConvertTimeFromUtc(utcNow, BrazilTime).ToString("dddd, d 'de' MMMM 'de' yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        var text = new StringBuilder($"Você está conversando com {name}. Hoje é {today}.");

        if (profile is { Instructions.Length: > 0 })
        {
            text.Append("\n\nOrientações da supervisão para o atendimento a esta atendente:\n<orientacoes_da_atendente>\n")
                .Append(profile.Instructions)
                .Append("\n</orientacoes_da_atendente>");
        }

        return text.ToString();
    }

    private static readonly TimeZoneInfo BrazilTime = FindBrazilTime();

    private static TimeZoneInfo FindBrazilTime()
    {
        foreach (var id in (string[])["America/Sao_Paulo", "E. South America Standard Time"])
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.Local;
    }
}

/// <summary>
/// Guarda da chave da API da IA. No Windows ela é cifrada com a proteção de dados da própria máquina
/// (DPAPI): o arquivo do banco, copiado para outro computador, não entrega a chave.
/// </summary>
public static class AssistantSecrets
{
    private const string Dpapi = "dpapi:";
    private const string Plain = "plain:";

    public static string Protect(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        return OperatingSystem.IsWindows()
            ? Dpapi + Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine))
            : Plain + Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string stored)
    {
        try
        {
            if (stored.StartsWith(Dpapi, StringComparison.Ordinal) && OperatingSystem.IsWindows())
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Dpapi.Length..]), null, DataProtectionScope.LocalMachine));
            }

            if (stored.StartsWith(Plain, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[Plain.Length..]));
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Banco trazido de outra máquina: a chave precisa ser informada de novo na Central.
        }

        return null;
    }

    /// <summary>A chave gravada pela Central ou, na falta dela, a de <c>Assistant:ApiKey</c> na configuração do servidor.</summary>
    public static async Task<string?> ReadKeyAsync(PontualDb db, IConfiguration configuration, CancellationToken ct)
    {
        var stored = await db.TextSettings.AsNoTracking()
            .Where(s => s.Key == TextSetting.AssistantApiKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(stored) && Unprotect(stored) is { Length: > 0 } key)
        {
            return key;
        }

        var configured = configuration["Assistant:ApiKey"];
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }
}
