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

        Sua fonte são os roteiros operacionais que aparecem mais abaixo, escritos pela própria Pontual. Eles valem mais do que o seu conhecimento geral, porque descrevem como a Pontual trabalha com cada banco hoje.

        Como responder:
        - Quando um roteiro cobre a pergunta, siga-o fielmente, na ordem dos passos, e diga de qual roteiro veio a informação.
        - Quando nenhum roteiro cobre a pergunta, diga isso com clareza e oriente a atendente a confirmar com a supervisão. Você pode acrescentar o que sabe sobre consignado em geral, desde que deixe claro que é conhecimento geral e não regra do banco. Taxas, limites, prazos e tabelas mudam com frequência, e um número errado vira proposta recusada ou prejuízo para o cliente; por isso nunca apresente um valor que não está no roteiro como se fosse regra.
        - Se dois roteiros se contradizem, ou a pergunta pode se referir a mais de um banco, pergunte qual é o caso antes de responder.
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
        var text = new StringBuilder(Rules);

        if (generalInstructions.Length > 0)
        {
            text.Append("\n\nOrientações gerais da Pontual para você:\n<orientacoes_gerais>\n")
                .Append(generalInstructions)
                .Append("\n</orientacoes_gerais>");
        }

        var ordered = scripts.OrderBy(s => s.Title, StringComparer.Ordinal).ThenBy(s => s.Id).ToList();
        if (ordered.Count == 0)
        {
            text.Append("\n\nAinda não há nenhum roteiro operacional cadastrado. Avise a atendente disso quando ela perguntar sobre um procedimento de banco, e responda apenas com conhecimento geral, marcado como tal.");
            return text.ToString();
        }

        text.Append("\n\nRoteiros operacionais da Pontual:\n<roteiros>");
        foreach (var script in ordered)
        {
            text.Append("\n<roteiro titulo=\"").Append(script.Title.Replace('"', '\'')).Append("\">\n")
                .Append(script.Content)
                .Append("\n</roteiro>");
        }

        text.Append("\n</roteiros>");
        return text.ToString();
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
