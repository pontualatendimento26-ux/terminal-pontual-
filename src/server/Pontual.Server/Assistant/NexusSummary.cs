using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pontual.Server.Assistant;

/// <summary>
/// Monta, a partir do resultado do Nexus, o texto que aparece no chat da atendente. É código, não IA: o banco, o
/// valor e o motivo saem exatamente como o roteiro do Nexus decidiu. Curto e em tabela: cliente, melhor opção, uma
/// linha por banco (quem passa primeiro) e, no fim, o contrato e o benefício ou a matrícula. O chat desenha as linhas
/// que começam com "|" como tabela; a IA lê a mesma tabela quando a atendente pergunta algo depois.
/// </summary>
public static class NexusSummary
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Brasilia = FindBrasilia();

    public static string SetorName(string setor) => setor switch
    {
        "siape" => "SIAPE",
        "inss" => "INSS",
        "exercito" => "Exército",
        _ => setor,
    };

    /// <summary>Resultado concluído (o corpo do 200 do Nexus).</summary>
    public static string Build(JsonElement r, string setor, bool fromCentral, DateTime? centralAt)
    {
        var text = new StringBuilder();
        var cliente = Obj(r, "cliente");
        var header = new List<string> { SetorName(setor) };
        if (Str(cliente, "nome") is { } nome) header.Add(nome);
        if (Int(cliente, "idade") is { } idade) header.Add($"{idade} anos");
        if (Str(cliente, "uf") is { } uf) header.Add(uf);
        text.Append("**").Append(string.Join(" · ", header)).Append("**\n");

        var melhor = Obj(r, "melhor_opcao");
        text.Append(melhor.ValueKind == JsonValueKind.Object
            ? $"**Melhor opção: {Str(melhor, "banco")} — {Money(Num(melhor, "valor_liberado"))}**"
            : "**Nenhum banco passa.**").Append("\n\n");

        var contratos = Arr(r, "contratos").ToList();
        var many = contratos.Count > 1;
        var labels = new Dictionary<string, string>();
        for (var i = 0; i < contratos.Count; i++) labels[Str(contratos[i], "id") ?? $"#{i}"] = $"C{i + 1}";

        // À vista, só o que decide: quem libera valor e quanto. ⚠️ = passa com atenção, 🔎 = conferir antes.
        var rows = Sorted(Arr(r, "roteiro")).ToList();
        var good = rows.Where(x => Str(x, "situacao") is "passa" or "atencao" or "conferir" && !(Num(x, "valor_liberado") is < 0)).ToList();
        if (good.Count > 0)
        {
            text.Append(many ? "| Banco | Contrato | Libera |\n|---|---|---|\n" : "| Banco | Libera |\n|---|---|\n");
            foreach (var row in good)
            {
                var mark = Str(row, "situacao") switch { "atencao" => " ⚠️", "conferir" => " 🔎", _ => "" };
                var cid = Str(row, "contrato_id");
                text.Append("| ").Append(Cell(Str(row, "banco") ?? "?")).Append(mark);
                if (many) text.Append(" | ").Append(cid is null ? "todos" : labels.GetValueOrDefault(cid, "?"));
                text.Append(" | ").Append(Num(row, "valor_liberado") is { } v ? Money(v) : "conferir").Append(" |\n");
            }

            text.Append('\n');
        }

        var bad = rows.Except(good).Select(x => Str(x, "banco") ?? "?").Distinct().ToList();
        if (bad.Count > 0) text.Append("Não passam: ").Append(string.Join(", ", bad)).Append('\n');
        if (good.Any(x => Str(x, "situacao") != "passa")) text.Append("⚠️ passa com atenção · 🔎 conferir antes de digitar\n");

        // O resto fica fechado no chat ("Ver detalhes"); a IA lê tudo.
        text.Append("\n[[detalhes]]\n");
        text.Append("CPF ").Append(NexusClient.FormatCpf(Str(cliente, "cpf") ?? "")).Append(" · ").Append(SourceNote(Obj(r, "fonte"), fromCentral, centralAt));
        if (Bool(cliente, "representante_legal") == true) text.Append(" · tem representante legal");
        text.Append("\n\n");
        text.Append(many ? "| Banco | Contrato | Resultado | Libera | Motivo |\n|---|---|---|---|---|\n" : "| Banco | Resultado | Libera | Motivo |\n|---|---|---|---|\n");
        foreach (var row in Sorted(Arr(r, "roteiro")))
        {
            var cid = Str(row, "contrato_id");
            var cells = new List<string> { Cell(Str(row, "banco") ?? "?") };
            if (many) cells.Add(cid is null ? "todos" : labels.GetValueOrDefault(cid, "?"));
            cells.Add(Label(Str(row, "situacao")));
            cells.Add(Num(row, "valor_liberado") is { } v ? Money(v) : "—");
            cells.Add(Cell(ShortReason(row)));
            text.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }

        text.Append('\n');
        for (var i = 0; i < contratos.Count; i++)
        {
            text.Append(many ? $"**C{i + 1}:** " : "**Contrato:** ").Append(ContractLine(contratos[i])).Append('\n');
        }

        var dados = Obj(r, "dados_setor");
        text.Append(setor switch
        {
            "inss" => Inss(dados),
            "siape" => Siape(dados),
            "exercito" => Exercito(dados),
            _ => "",
        });

        // Só os avisos que mudam o que a atendente faz; "reaproveitado" já está no topo.
        var avisos = Arr(r, "avisos").Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null).OfType<string>()
            .Where(a => !a.StartsWith("Resultado reaproveitado", StringComparison.OrdinalIgnoreCase)).ToList();
        if (avisos.Count > 0)
        {
            text.Append("\n**Atenção:**\n");
            foreach (var aviso in avisos) text.Append("- ").Append(aviso).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>O Nexus (ou o seu cache) não achou o CPF no setor.</summary>
    public static string NotFound(string cpf, string setor, string mensagem, bool paga, bool fromCentral, DateTime? centralAt)
    {
        var text = new StringBuilder($"**Consulta {SetorName(setor)}: CPF não encontrado**\nCPF {NexusClient.FormatCpf(cpf)} · ");
        text.Append(fromCentral && centralAt is { } at ? $"guardado de {Local(at)}, sem custo" : paga ? "consulta paga" : "sem custo");
        text.Append("\n\n").Append(mensagem.Length > 0 ? mensagem : $"CPF não encontrado no {SetorName(setor)}.");
        text.Append("\nSe o cliente for de outro setor, consulte de novo escolhendo o setor certo.");
        return text.ToString();
    }

    private static string SourceNote(JsonElement fonte, bool fromCentral, DateTime? centralAt)
    {
        if (fromCentral && centralAt is { } at)
        {
            return $"guardado de {Local(at)}, sem custo";
        }

        var when = Str(fonte, "consultado_em") is { } iso && DateTimeOffset.TryParse(iso, Br, DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM HH:mm", Br) : null;
        var origem = Str(fonte, "origem") == "cache" ? $"do Nexus{(when is null ? "" : $" de {when}")}" : "consultado agora";
        return origem + (Bool(fonte, "paga") == true ? ", consulta paga" : ", sem custo");
    }

    private static IEnumerable<JsonElement> Sorted(IEnumerable<JsonElement> rows) =>
        rows.OrderBy(x => Str(x, "situacao") switch { "passa" => 0, "atencao" => 1, "conferir" => 2, _ => 3 })
            .ThenByDescending(x => Num(x, "valor_liberado") ?? decimal.MinValue);

    private static string Label(string? situacao) => situacao switch
    {
        "passa" => "✅ Passa",
        "atencao" => "⚠️ Atenção",
        "conferir" => "🔎 Conferir",
        "nao_passa" => "❌ Não passa",
        _ => situacao ?? "?",
    };

    /// <summary>O motivo sem o óbvio: "Passa no roteiro do X" some; o que vem depois do ";" fica.</summary>
    private static string ShortReason(JsonElement row)
    {
        var motivo = Str(row, "motivo") ?? "";
        if (Str(row, "situacao") == "passa" && motivo.StartsWith("Passa no roteiro", StringComparison.OrdinalIgnoreCase))
        {
            var cut = motivo.IndexOf(';');
            return cut < 0 ? "" : motivo[(cut + 1)..].Trim();
        }

        return motivo;
    }

    private static string Cell(string value) => value.Replace('|', '/').ReplaceLineEndings(" ").Trim();

    private static string ContractLine(JsonElement c)
    {
        var parts = new List<string>();
        var banco = Obj(c, "banco_origem");
        var nome = Str(banco, "nome");
        var codigo = Str(banco, "codigo");
        parts.Add(nome is null ? codigo ?? "banco não informado" : codigo is null ? nome : $"{nome} ({codigo})");
        if (Num(c, "parcela") is { } parcela) parts.Add($"parcela {Money(parcela)}");
        var pagas = Int(c, "parcelas_pagas");
        var prazo = Int(c, "prazo");
        parts.Add(pagas is null ? "pagas: não informado" : prazo is null ? $"{pagas} pagas" : $"{pagas}/{prazo} pagas");
        if (Num(c, "saldo_devedor") is { } saldo) parts.Add($"saldo {Money(saldo)}");
        if (Num(c, "taxa") is { } taxa) parts.Add($"taxa {taxa.ToString("0.00", Br)}%");
        if (Bool(c, "intencionado") == true) parts.Add("JÁ NA ESTEIRA");
        return string.Join(" · ", parts);
    }

    private static string Inss(JsonElement dados)
    {
        var text = new StringBuilder();
        foreach (var b in Arr(dados, "beneficios"))
        {
            var parts = new List<string>();
            var especie = Obj(b, "especie");
            var esp = string.Join(" ", new[] { Str(especie, "codigo"), Str(especie, "nome") }.OfType<string>());
            if (esp.Length > 0) parts.Add($"espécie {esp}");
            if (Bool(b, "bloqueado") == true) parts.Add("BLOQUEADO");
            if (Num(b, "valor_beneficio") is { } valor) parts.Add($"valor {Money(valor)}");
            var m = Obj(b, "margens");
            parts.Add($"margem {Money(Num(m, "emprestimo"))} · RMC {Money(Num(m, "rmc"))} · RCC {Money(Num(m, "rcc"))}");
            text.Append("**Benefício:** ").Append(string.Join(" · ", parts)).Append('\n');
        }

        return text.ToString();
    }

    private static string Siape(JsonElement dados)
    {
        var text = new StringBuilder();
        foreach (var mat in Arr(dados, "matriculas"))
        {
            var parts = new List<string>();
            if (Str(Obj(mat, "orgao"), "nome") is { } orgao) parts.Add(orgao);
            if (Str(mat, "situacao_funcional") is { } sit) parts.Add(sit);
            var m = Obj(mat, "margens");
            parts.Add($"margem {Money(Num(m, "consignavel"))} · cartão {Money(Num(m, "cartao"))}");
            text.Append("**Matrícula:** ").Append(string.Join(" · ", parts)).Append('\n');
        }

        return text.ToString();
    }

    private static string Exercito(JsonElement dados)
    {
        if (dados.ValueKind != JsonValueKind.Object) return "";
        var parts = new List<string>();
        if (Str(dados, "posto") is { } posto) parts.Add(posto);
        var m = Obj(dados, "margens");
        parts.Add($"margem {Money(Num(m, "consignavel"))} · liberando {Money(Num(m, "liberando"))}");
        return $"**Matrícula:** {string.Join(" · ", parts)}\n";
    }

    private static string Money(decimal? value) => value is { } v ? v.ToString("C2", Br) : "—";

    private static string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Brasilia).ToString("dd/MM HH:mm", Br);

    private static TimeZoneInfo FindBrasilia()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
    }

    // ---------------------------------------------------------------- leitura do JSON (tudo pode vir nulo)

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static decimal? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
