using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Pontual.Server.Api;
using Pontual.Server.Data;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Pontual.Server.Assistant;

/// <summary>Tetos da base de conhecimento. Nada é cortado em silêncio: o que passa do teto é recusado com aviso.</summary>
public static class KnowledgeLimits
{
    /// <summary>Tamanho máximo de um conhecimento (um documento longo cabe; um livro inteiro deve ser dividido).</summary>
    public const int Item = 600_000;
    public const int All = 12_000_000;
    public const int Items = 2000;
    public const int Upload = 20 * 1024 * 1024;

    /// <summary>
    /// Até este total de caracteres ligados, a IA recebe a base inteira a cada pergunta (mais preciso, e o
    /// cache torna barato). Acima disso ela recebe o índice e abre só o que a pergunta pede.
    /// </summary>
    public const int InlineBudget = 200_000;

    /// <summary>No modo índice, um conhecimento longo é lido em partes deste tamanho.</summary>
    public const int Part = 24_000;

    /// <summary>Quanto texto a IA pode abrir da base para responder uma única pergunta.</summary>
    public const int OpenedPerQuestion = 150_000;
    public const int ToolCallsPerQuestion = 12;
}

public static class KnowledgeText
{
    /// <summary>Ordem fixa (título, depois id): o texto enviado à IA sai idêntico a cada pergunta, e o cache vale.</summary>
    public static List<AssistantScript> Ordered(IEnumerable<AssistantScript> items) =>
        [.. items.OrderBy(s => s.Title, StringComparer.Ordinal).ThenBy(s => s.Id)];

    /// <summary>Divide um texto longo em partes, cortando em fim de parágrafo sempre que possível.</summary>
    public static List<string> Parts(string content)
    {
        const int size = KnowledgeLimits.Part;
        if (content.Length <= size)
        {
            return [content];
        }

        var parts = new List<string>();
        var start = 0;
        while (content.Length - start > size)
        {
            var end = start + size;
            var cut = content.LastIndexOf("\n\n", end - 1, size / 2, StringComparison.Ordinal);
            if (cut <= start)
            {
                cut = content.LastIndexOf('\n', end - 1, size / 2);
            }

            if (cut <= start)
            {
                cut = end;
            }

            parts.Add(content[start..cut].Trim());
            start = cut;
        }

        parts.Add(content[start..].Trim());
        parts.RemoveAll(p => p.Length == 0);
        return parts;
    }

    /// <summary>Minúsculas e sem acento, um caractere por caractere (as posições continuam valendo no original).</summary>
    public static string Fold(string text)
    {
        var folded = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c < 128)
            {
                folded[i] = char.ToLowerInvariant(c);
            }
            else if (char.IsSurrogate(c))
            {
                folded[i] = c;
            }
            else
            {
                var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
                folded[i] = char.ToLowerInvariant(decomposed[0]);
            }
        }

        return new string(folded);
    }

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "a", "o", "as", "os", "e", "que", "para", "pra", "com", "como", "em", "no", "na",
        "nos", "nas", "um", "uma", "por", "se", "ao", "ou", "eu", "me", "qual", "quais", "tem", "ser", "foi",
    };

    public static List<string> Terms(string query) =>
    [
        .. Fold(query).Split([' ', '\n', '\t', ',', '.', ';', ':', '?', '!', '(', ')', '"', '/', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && !Common.Contains(t)).Distinct().Take(12),
    ];

    public sealed record Hit(AssistantScript Item, int Part, int Parts, string Snippet, double Score);

    /// <summary>
    /// Busca por palavras, sem diferenciar acento nem maiúscula. Vale mais o item que casa mais palavras
    /// diferentes; título, banco e palavras-chave pesam mais que o corpo do texto.
    /// </summary>
    public static List<Hit> Search(IEnumerable<AssistantScript> items, string query, int max, int snippet)
    {
        var terms = Terms(query);
        if (terms.Count == 0)
        {
            return [];
        }

        var phrase = Fold(query.Trim());
        var hits = new List<Hit>();
        foreach (var item in items)
        {
            var head = Fold($"{item.Title} {item.Bank} {item.Category} {item.Keywords} {item.Description}");
            var parts = Parts(item.Content);
            Hit? best = null;
            for (var p = 0; p < parts.Count; p++)
            {
                var body = Fold(parts[p]);
                double score = 0;
                var matched = 0;
                var first = -1;
                foreach (var term in terms)
                {
                    var inHead = head.Contains(term, StringComparison.Ordinal);
                    var count = Count(body, term, out var at);
                    if (!inHead && count == 0)
                    {
                        continue;
                    }

                    matched++;
                    score += (inHead ? 6 : 0) + Math.Min(count, 5);
                    if (first < 0 && at >= 0)
                    {
                        first = at;
                    }
                }

                if (matched == 0)
                {
                    continue;
                }

                if (phrase.Length > 3 && terms.Count > 1)
                {
                    var whole = body.IndexOf(phrase, StringComparison.Ordinal);
                    if (whole >= 0)
                    {
                        score += 8;
                        first = whole;
                    }
                }

                score += matched * 20;
                if (best is null || score > best.Score)
                {
                    best = new Hit(item, p + 1, parts.Count, Snippet(parts[p], Math.Max(first, 0), snippet), score);
                }
            }

            if (best is not null)
            {
                hits.Add(best);
            }
        }

        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Item.Title, StringComparer.Ordinal).Take(max)];
    }

    private static int Count(string text, string term, out int first)
    {
        first = -1;
        var count = 0;
        for (var at = text.IndexOf(term, StringComparison.Ordinal); at >= 0 && count < 6; at = text.IndexOf(term, at + term.Length, StringComparison.Ordinal))
        {
            if (first < 0)
            {
                first = at;
            }

            count++;
        }

        return count;
    }

    private static string Snippet(string text, int around, int size)
    {
        if (text.Length <= size)
        {
            return text.Trim();
        }

        var start = Math.Clamp(around - size / 3, 0, text.Length - size);
        var piece = text.Substring(start, size).Trim();
        return (start > 0 ? "… " : "") + piece + (start + size < text.Length ? " …" : "");
    }
}

/// <summary>Uma ferramenta que a IA pode chamar; a definição não depende do SDK.</summary>
public sealed record AssistantTool(string Name, string Description, Dictionary<string, JsonElement> Properties, string[] Required);

public interface IAssistantTools
{
    IReadOnlyList<AssistantTool> Definitions { get; }

    /// <summary>Executa a ferramenta. <c>Status</c> é a frase curta mostrada à atendente enquanto ela espera.</summary>
    (string Result, string Status) Run(string name, IReadOnlyDictionary<string, JsonElement> input);
}

/// <summary>
/// Modo índice: a IA vê a lista numerada dos conhecimentos e usa estas ferramentas para ler o texto.
/// O número é a posição na lista ordenada, a mesma que aparece no índice enviado à IA.
/// </summary>
public sealed class KnowledgeTools(IReadOnlyList<AssistantScript> ordered) : IAssistantTools
{
    public const string Open = "abrir_conhecimento";
    public const string Find = "buscar_na_base";

    private int _calls;
    private int _opened;

    public IReadOnlyList<AssistantTool> Definitions { get; } =
    [
        new(Open,
            "Abre o texto completo de um conhecimento do índice (roteiro, documento ou conhecimento operacional). Use antes de responder qualquer pergunta sobre regra, passo a passo, prazo ou documento exigido: o índice traz só o título e o resumo, nunca a regra. Abra todos os itens que a pergunta envolve na mesma rodada.",
            new Dictionary<string, JsonElement>
            {
                ["numero"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Número do conhecimento no índice." }),
                ["parte"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Parte a abrir, quando o índice diz que o conhecimento tem mais de uma. Sem este campo, abre a parte 1." }),
            },
            ["numero"]),
        new(Find,
            "Procura um texto em toda a base da Pontual e devolve os trechos mais parecidos, com o número e a parte de cada conhecimento. Use quando o índice não deixa claro onde está a resposta.",
            new Dictionary<string, JsonElement>
            {
                ["texto"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Palavras a procurar, como a atendente escreveria. Ex.: \"margem cartão benefício Safra\"." }),
            },
            ["texto"]),
    ];

    public (string Result, string Status) Run(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (++_calls > KnowledgeLimits.ToolCallsPerQuestion || _opened >= KnowledgeLimits.OpenedPerQuestion)
        {
            return ("Limite de consultas desta pergunta atingido. Responda com o que já foi aberto e diga à atendente o que faltou conferir.", "");
        }

        if (name == Find)
        {
            var text = input.TryGetValue("texto", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var hits = KnowledgeText.Search(ordered, text, 8, 700);
            if (hits.Count == 0)
            {
                return ("Nada encontrado na base para essas palavras.", "Procurando na base…");
            }

            var found = new StringBuilder();
            foreach (var hit in hits)
            {
                var number = IndexOf(hit.Item) + 1;
                found.Append($"[{number}] {hit.Item.Title} (parte {hit.Part} de {hit.Parts})\n{hit.Snippet}\n\n");
            }

            _opened += found.Length;
            return (found.Append("Os trechos acima são recortes. Abra o conhecimento antes de afirmar uma regra.").ToString(), "Procurando na base…");
        }

        if (name == Open)
        {
            var number = Integer(input, "numero");
            if (number < 1 || number > ordered.Count)
            {
                return ($"Não existe conhecimento número {number}. Os números vão de 1 a {ordered.Count}.", "");
            }

            var item = ordered[number - 1];
            var parts = KnowledgeText.Parts(item.Content);
            var part = Math.Max(Integer(input, "parte"), 1);
            if (part > parts.Count)
            {
                return ($"\"{item.Title}\" tem {parts.Count} parte(s); a parte {part} não existe.", "");
            }

            var text = AssistantPrompt.Render(item, parts[part - 1], parts.Count > 1 ? $"parte {part} de {parts.Count}" : null);
            _opened += text.Length;
            return (text, $"Consultando {item.Title}…");
        }

        return ($"Ferramenta desconhecida: {name}.", "");
    }

    private int IndexOf(AssistantScript item)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Id == item.Id)
            {
                return i;
            }
        }

        return -1;
    }

    private static int Integer(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}

public sealed record ExtractedText(string Text, string Status, string Error);

/// <summary>Tira o texto de um arquivo enviado (PDF com texto, DOCX ou TXT).</summary>
public static class DocumentText
{
    public static readonly string[] Extensions = [".pdf", ".docx", ".txt"];

    public const string ScannedPdf =
        "O PDF não tem texto para ler: parece escaneado (imagem). Ele fica guardado e desligado até o leitor de PDF escaneado ser conectado.";

    public static ExtractedText Extract(string fileName, byte[] bytes, ILogger logger)
    {
        string text;
        try
        {
            switch (Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".txt":
                    text = Plain(bytes);
                    break;
                case ".docx":
                    text = Docx(bytes);
                    break;
                case ".pdf":
                    text = Pdf(bytes, out var pages);
                    if (text.Count(char.IsLetterOrDigit) < 40 * Math.Max(pages, 1))
                    {
                        return new ExtractedText("", FileStatus.NoText, ScannedPdf);
                    }

                    break;
                default:
                    return new ExtractedText("", FileStatus.Error, "Formato não aceito. Envie PDF, DOCX ou TXT.");
            }
        }
        // Leitores de arquivo de terceiros falham de muitos jeitos com arquivo corrompido; todos viram o mesmo aviso.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Falha ao extrair o texto de {File}", fileName);
            return new ExtractedText("", FileStatus.Error, "Não foi possível ler o arquivo: ele parece corrompido ou protegido por senha.");
        }

        text = Tidy(text);
        if (text.Length == 0)
        {
            return new ExtractedText("", FileStatus.Error, "O arquivo não tem texto.");
        }

        if (text.Length > KnowledgeLimits.Item)
        {
            return new ExtractedText("", FileStatus.Error,
                $"O arquivo tem {text.Length:N0} caracteres de texto; o máximo por documento é {KnowledgeLimits.Item:N0}. Divida o arquivo em partes.");
        }

        return new ExtractedText(text, FileStatus.Processed, "");
    }

    /// <summary>Limpa caracteres de controle e junta linhas em branco repetidas.</summary>
    private static string Tidy(string text)
    {
        var clean = AssistantEndpoints.Multiline(text);
        var builder = new StringBuilder(clean.Length);
        var blank = 0;
        foreach (var line in clean.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            blank = trimmed.Length == 0 ? blank + 1 : 0;
            if (blank <= 1)
            {
                builder.Append(trimmed).Append('\n');
            }
        }

        return builder.ToString().Trim();
    }

    private static string Plain(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            // Arquivo de texto antigo do Windows.
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string Pdf(byte[] bytes, out int pages)
    {
        using var pdf = PdfDocument.Open(bytes);
        pages = pdf.NumberOfPages;
        var text = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            text.Append(ContentOrderTextExtractor.GetText(page)).Append("\n\n");
        }

        return text.ToString();
    }

    private static string Docx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("word/document.xml ausente");
        if (entry.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("documento grande demais");
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var text = new StringBuilder();
        foreach (var paragraph in document.Descendants(w + "p"))
        {
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == w + "t")
                {
                    text.Append(node.Value);
                }
                else if (node.Name == w + "tab")
                {
                    text.Append('\t');
                }
                else if (node.Name == w + "br" || node.Name == w + "cr")
                {
                    text.Append('\n');
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
