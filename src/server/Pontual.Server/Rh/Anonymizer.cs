using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pontual.Server.Rh;

/// <summary>
/// Tira os dados pessoais de um texto ANTES de ele chegar ao Agente RH: CPF, CNPJ, telefone, e-mail, número de
/// contrato/benefício/conta e os nomes conhecidos do cliente e da equipe. É feito por regra fixa, sem IA: o mesmo
/// texto dá sempre o mesmo resultado, e nada sai da Central para ser limpo.
/// Limite conhecido: um nome que ninguém informou e aparece solto no meio da frase pode passar.
/// Valores em dinheiro, datas e horários ficam, porque o caso precisa deles para fazer sentido.
/// </summary>
public static partial class Anonymizer
{
    public const string Cpf = "[CPF]";
    public const string Cnpj = "[CNPJ]";
    public const string Phone = "[TELEFONE]";
    public const string Email = "[EMAIL]";
    public const string Number = "[NUMERO]";
    public const string Client = "[CLIENTE]";
    public const string Staff = "[ATENDENTE]";
    public const string Name = "[NOME]";

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailRx();

    [GeneratedRegex(@"(?<!\d)\d{2}\.?\d{3}\.?\d{3}/\d{4}-?\d{2}(?!\d)")]
    private static partial Regex CnpjRx();

    [GeneratedRegex(@"(?<!\d)\d{3}\.\d{3}\.\d{3}\s?-?\s?\d{2}(?!\d)|(?<!\d)\d{9}-\d{2}(?!\d)")]
    private static partial Regex CpfFormattedRx();

    [GeneratedRegex(@"(?<!\d)(?<!\d[.,])\d{11}(?!\d)")]
    private static partial Regex ElevenDigitsRx();

    [GeneratedRegex(@"(?<!\d)(?<!\d[.,])(?:\+?55[\s.-]?)?(?:\(\d{2}\)|\d{2})[\s.-]?(?:9[\s.-]?)?\d{4}[\s.-]?\d{4}(?!\d)")]
    private static partial Regex PhoneRx();

    /// <summary>Qualquer sequência longa de dígitos (com ponto, traço ou barra no meio) que tenha sobrado.</summary>
    [GeneratedRegex(@"(?<!\d)(?<!\d[.,])\d[\d.\-/]{4,}\d")]
    private static partial Regex LongNumberRx();

    [GeneratedRegex(@"^\d{1,2}/\d{1,2}(?:/\d{2,4})?$|^\d{1,2}/\d{4}$|^\d{1,2}-\d{1,2}-\d{2,4}$")]
    private static partial Regex DateRx();

    [GeneratedRegex(@"^\d{1,3}(?:\.\d{3})+$")]
    private static partial Regex ThousandsRx();

    /// <summary>"meu nome é Fulano", "me chamo Fulano", "sou a Fulana", "aqui é o Fulano": o nome sai mesmo sem ninguém ter informado.</summary>
    [GeneratedRegex(@"(?<antes>(?i:meu nome (?:é|e)|me chamo|aqui (?:é|e)(?: o| a)?|sou (?:o|a))\s+)(?!Banco|Pontual|Caixa|INSS)(?<nome>\p{Lu}\p{Ll}+(?:\s+(?:d[aeo]s?\s+)?\p{Lu}\p{Ll}+){0,3})")]
    private static partial Regex IntroducedRx();

    /// <summary>Palavras que aparecem no lugar do nome no cadastro do CRM e não são nome de ninguém.</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "da", "de", "do", "das", "dos", "del", "cliente", "contato", "whatsapp", "lead", "novo", "nova", "sem", "nome",
        "pontual", "consig", "banco", "externo", "atendente", "equipe", "sistema", "suporte",
    };

    private static readonly Dictionary<char, string> Accents = new()
    {
        ['a'] = "[aáàâãä]", ['e'] = "[eéèêë]", ['i'] = "[iíìîï]", ['o'] = "[oóòôõö]", ['u'] = "[uúùûü]", ['c'] = "[cç]", ['n'] = "[nñ]",
    };

    /// <summary>
    /// O texto sem dados pessoais. <paramref name="clientNames"/> e <paramref name="staffNames"/> são os nomes que a
    /// Central já conhece (o contato no CRM, quem digitou, as atendentes cadastradas): cada parte do nome sai.
    /// </summary>
    public static string Clean(string? text, IEnumerable<string>? clientNames = null, IEnumerable<string>? staffNames = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var clean = EmailRx().Replace(text, Email);
        clean = CnpjRx().Replace(clean, Cnpj);
        clean = CpfFormattedRx().Replace(clean, Cpf);
        // Onze dígitos seguidos: CPF se os dígitos verificadores conferem; senão é celular com DDD, que a regra de telefone pega.
        clean = ElevenDigitsRx().Replace(clean, m => ValidCpf(m.Value) ? Cpf : m.Value);
        clean = PhoneRx().Replace(clean, Phone);
        clean = LongNumberRx().Replace(clean, m => KeepNumber(clean, m) ? m.Value : Number);

        clean = ReplaceNames(clean, clientNames, Client);
        clean = ReplaceNames(clean, staffNames, Staff);
        clean = IntroducedRx().Replace(clean, m => m.Groups["antes"].Value + Name);
        return clean;
    }

    /// <summary>Data, valor em dinheiro e número curto ficam; o resto (contrato, benefício, conta, CEP) sai.</summary>
    private static bool KeepNumber(string text, Match match)
    {
        var value = match.Value;
        if (DateRx().IsMatch(value) || ThousandsRx().IsMatch(value))
        {
            return true;
        }

        // "123456,78": valor com centavos.
        var after = match.Index + match.Length;
        if (after + 2 < text.Length && text[after] == ',' && char.IsDigit(text[after + 1]) && char.IsDigit(text[after + 2])
            && (after + 3 >= text.Length || !char.IsDigit(text[after + 3])) && value.All(char.IsDigit))
        {
            return true;
        }

        return value.Count(char.IsDigit) < 6;
    }

    private static bool ValidCpf(string digits)
    {
        if (digits.Length != 11 || digits.Distinct().Count() == 1)
        {
            return false;
        }

        int Check(int length)
        {
            var sum = 0;
            for (var i = 0; i < length; i++)
            {
                sum += (digits[i] - '0') * (length + 1 - i);
            }

            var rest = sum * 10 % 11;
            return rest == 10 ? 0 : rest;
        }

        return Check(9) == digits[9] - '0' && Check(10) == digits[10] - '0';
    }

    private static string ReplaceNames(string text, IEnumerable<string>? names, string mark)
    {
        var tokens = (names ?? [])
            .SelectMany(name => (name ?? "").Split([' ', '\t', '-', '.', ',', '(', ')', '/', '_'], StringSplitOptions.RemoveEmptyEntries))
            .Select(Fold)
            .Where(token => token.Length >= 3 && token.All(char.IsLetter) && !NotNames.Contains(token))
            .Distinct()
            .OrderByDescending(token => token.Length)
            .ToList();
        if (tokens.Count == 0)
        {
            return text;
        }

        var pattern = string.Join('|', tokens.Select(token => string.Concat(token.Select(c => Accents.TryGetValue(c, out var set) ? set : Regex.Escape(c.ToString())))));
        var replaced = Regex.Replace(text, $@"(?<![\p{{L}}\[])(?:{pattern})(?![\p{{L}}\]])", mark, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // "[CLIENTE] da [CLIENTE] [CLIENTE]" vira um "[CLIENTE]" só.
        var escaped = Regex.Escape(mark);
        return Regex.Replace(replaced, $@"{escaped}(?:\s+(?:(?i:d[aeo]s?|e)\s+)?{escaped})+", mark);
    }

    /// <summary>Minúsculas e sem acento, para comparar nomes.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}
