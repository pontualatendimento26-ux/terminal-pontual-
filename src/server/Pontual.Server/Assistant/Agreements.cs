using System.Text;
using System.Text.Json;
using Pontual.Server.Api;
using Pontual.Server.Data;

namespace Pontual.Server.Assistant;

/// <summary>
/// Dados estruturados de um convênio de consignado (prefeitura, governo, tribunal...), guardados em
/// <see cref="AssistantScript.DetailsJson"/> como chave → texto. Aqui ficam a lista de campos conhecidos, a
/// validação do que a Central Neural manda e a versão em texto que vai para a IA.
/// </summary>
public static class Agreements
{
    public const int MaxFields = 40;
    public const int MaxKey = 40;
    public const int MaxValue = 1000;
    public const int MaxRank = 99;
    public const int MaxNote = 200;

    /// <summary>Campos na ordem em que aparecem no texto e nos cartões: chave e rótulo.</summary>
    public static readonly (string Key, string Label)[] Fields =
    [
        ("cidade", "Cidade ou âmbito"),
        ("tipo", "Tipo de órgão"),
        ("orgao", "Órgão"),
        ("publico", "Público-alvo"),
        ("nao_aceitos", "Não aceitos"),
        ("prazo", "Prazo máximo"),
        ("margem", "Margem"),
        ("margem_seguranca", "Margem de segurança"),
        ("idade_valor", "Idade x valor"),
        ("qtd_contratos", "Quantidade de contratos"),
        ("venda_digital", "Venda digital"),
        ("averbadora", "Averbadora"),
        ("codigo_online", "Código online"),
        ("abater_neg", "Abater margem negativa"),
        ("agregar_refin", "Agregar margem no refin"),
        ("unificar_refin", "Unificar parcelas no refin"),
        ("portab", "Portabilidade (agregar / unificar)"),
        ("docs", "Documentos"),
        ("atencao", "Atenção"),
        ("simplificado", "Roteiro Simplificado no portal"),
        ("lido", "Roteiro lido"),
        ("ultima_data", "Data do roteiro"),
        ("arquivos", "Arquivo no portal do banco"),
        ("pagina", "Página aproximada na lista do portal"),
    ];

    private static readonly Dictionary<string, string> Labels = Fields.ToDictionary(f => f.Key, f => f.Label);

    /// <summary>Lê o JSON guardado. Vazio ou inválido vira dicionário vazio (nunca derruba a tela).</summary>
    public static Dictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                    _ => "",
                };
                if (value.Length > 0)
                {
                    result[property.Name] = value;
                }
            }
        }
        catch (JsonException)
        {
            // JSON estragado à mão: melhor cartão sem dados do que Central fora do ar.
        }

        return result;
    }

    /// <summary>
    /// Valida o que a Central mandou e devolve o JSON a guardar (vazio quando não sobrou nada). Chaves fora da
    /// lista são aceitas (um banco novo pode ter uma regra que os outros não têm), mas com tamanho limitado.
    /// </summary>
    public static string Normalize(Dictionary<string, string?>? details)
    {
        if (details is null || details.Count == 0)
        {
            return "";
        }

        if (details.Count > MaxFields)
        {
            throw ApiError.BadRequest($"Os dados do convênio aceitam no máximo {MaxFields} campos.");
        }

        var clean = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in details)
        {
            var key = Input.Clean(rawKey).ToLowerInvariant().Replace(' ', '_');
            if (key.Length == 0 || key.Length > MaxKey || !key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                throw ApiError.BadRequest("Um campo do convênio tem nome inválido. Use letras minúsculas, números e sublinhado.");
            }

            var value = Input.Clean(rawValue);
            if (value.Length > MaxValue)
            {
                throw ApiError.BadRequest($"O campo \"{Label(key)}\" do convênio aceita no máximo {MaxValue} caracteres.");
            }

            if (value.Length > 0)
            {
                clean[key] = value;
            }
        }

        return clean.Count == 0 ? "" : JsonSerializer.Serialize(clean);
    }

    public static string Label(string key) => Labels.TryGetValue(key, out var label) ? label : key.Replace('_', ' ');

    /// <summary>
    /// O convênio em texto corrido, do jeito que a IA lê: uma linha por campo, na ordem conhecida, e depois os
    /// campos extras. É este texto que vira o conteúdo do roteiro quando ele nasce da carga inicial.
    /// </summary>
    public static string ToText(string bank, string uf, Dictionary<string, string> details)
    {
        var text = new StringBuilder();
        if (details.TryGetValue("orgao", out var organ) && organ.Length > 0)
        {
            text.Append("Convênio: ").Append(organ);
            if (uf.Length > 0)
            {
                text.Append(" (").Append(uf).Append(')');
            }

            text.Append('\n');
        }

        if (bank.Length > 0)
        {
            text.Append("Banco: ").Append(bank).Append('\n');
        }

        var written = new HashSet<string>(StringComparer.Ordinal) { "orgao" };
        foreach (var (key, label) in Fields)
        {
            if (key != "orgao" && details.TryGetValue(key, out var value) && value.Length > 0)
            {
                text.Append(label).Append(": ").Append(value).Append('\n');
                written.Add(key);
            }
        }

        foreach (var (key, value) in details.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (!written.Contains(key))
            {
                text.Append(Label(key)).Append(": ").Append(value).Append('\n');
            }
        }

        if (details.TryGetValue("lido", out var read) && read.Equals("Não", StringComparison.OrdinalIgnoreCase))
        {
            text.Append("\nAtenção: o roteiro completo deste convênio ainda não foi lido pela Pontual; só se sabe o nome do arquivo no portal do banco. Para prazo, margem e regras, a atendente deve abrir o roteiro no portal ou confirmar com a supervisão.\n");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>"1º do banco (mais vendido)", "2º do banco"...; vazio sem ranking.</summary>
    public static string RankLabel(int rank) => rank switch
    {
        <= 0 => "",
        1 => "1º do banco (o roteiro que mais vende)",
        _ => $"{rank}º do banco em vendas",
    };
}
