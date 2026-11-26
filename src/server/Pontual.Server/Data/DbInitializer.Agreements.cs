using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pontual.Domain.Audit;
using Pontual.Server.Api;
using Pontual.Server.Assistant;

namespace Pontual.Server.Data;

/// <summary>Carga inicial dos convênios de consignado (ver <see cref="SeedAgreementsAsync"/>).</summary>
public static partial class DbInitializer
{
    /// <summary>
    /// Carrega os convênios de consignado da pasta <c>convenios-iniciais</c>: cada arquivo .json é a lista de
    /// convênios de um banco (o mesmo JSON do painel de roteiros), e cada convênio vira um roteiro da assistente,
    /// com os dados estruturados guardados e a versão em texto no conteúdo. Cada arquivo entra uma única vez (o
    /// nome fica guardado); depois disso quem manda é a Central Neural, inclusive para apagar.
    /// </summary>
    private static async Task SeedAgreementsAsync(PontualDb db, IServiceProvider services)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "convenios-iniciais");
        if (!Directory.Exists(folder))
        {
            return;
        }

        var mark = await db.TextSettings.FirstOrDefaultAsync(s => s.Key == TextSetting.AssistantAgreementsSeeded);
        var loaded = new HashSet<string>((mark?.Value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var count = await db.AssistantScripts.CountAsync();
        var added = 0;
        foreach (var file in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            if (!loaded.Add(Path.GetFileName(file)))
            {
                continue;
            }

            List<Dictionary<string, JsonElement>>? rows;
            try
            {
                rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(await File.ReadAllTextAsync(file));
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var row in rows ?? [])
            {
                if (count + added >= KnowledgeLimits.Items)
                {
                    break;
                }

                var script = AgreementToScript(row, now);
                if (script is not null)
                {
                    db.AssistantScripts.Add(script);
                    added++;
                }
            }
        }

        var value = string.Join('\n', loaded.Order(StringComparer.Ordinal));
        if (mark is null)
        {
            db.TextSettings.Add(new TextSetting { Key = TextSetting.AssistantAgreementsSeeded, Value = value });
        }
        else
        {
            mark.Value = value;
        }

        if (added == 0)
        {
            await db.SaveChangesAsync();
            return;
        }

        await services.GetRequiredService<AuditWriter>().SaveAsync(
            db,
            new AuditInfo(ActorType.System, null, "Sistema", "assistant.agreement.seed",
                $"Sistema cadastrou {added} roteiro(s) de convênio na base da assistente"));
    }

    /// <summary>Uma linha do JSON do painel → um roteiro. Nulo quando falta banco ou órgão.</summary>
    public static AssistantScript? AgreementToScript(Dictionary<string, JsonElement> row, DateTime now)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in row)
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => Input.Clean(value.GetString()),
                JsonValueKind.Number => value.GetRawText(),
                _ => "",
            };
            if (text.Length > 0 && key is not ("id" or "banco" or "uf" or "estado" or "uf_ordem"))
            {
                details[key] = text.Length > Agreements.MaxValue ? text[..Agreements.MaxValue] : text;
            }
        }

        var bank = row.TryGetValue("banco", out var b) && b.ValueKind == JsonValueKind.String ? Input.Clean(b.GetString()) : "";
        var uf = row.TryGetValue("uf", out var u) && u.ValueKind == JsonValueKind.String ? Input.Clean(u.GetString()).ToUpperInvariant() : "";
        if (bank.Length == 0 || !details.TryGetValue("orgao", out var organ))
        {
            return null;
        }

        var read = details.TryGetValue("lido", out var l) && l.Equals("Sim", StringComparison.OrdinalIgnoreCase);
        var kind = details.GetValueOrDefault("tipo", "Convênio");
        var city = details.GetValueOrDefault("cidade", "");
        var generic = city.Length == 0 || city.StartsWith("Esfera", StringComparison.OrdinalIgnoreCase) || city.StartsWith("Não consta", StringComparison.OrdinalIgnoreCase);
        var place = generic ? kind : $"{kind} de {city}";
        return new AssistantScript
        {
            Id = Guid.NewGuid(), Kind = KnowledgeKind.Script, Title = Input.Truncated($"{bank} - {organ} ({uf})", 120),
            Content = Agreements.ToText(bank, uf, details), Bank = bank, Uf = uf, Category = "Consignado público",
            Description = read
                ? $"{place}, {uf}: prazo {details.GetValueOrDefault("prazo_meses", "?")} meses, margem {details.GetValueOrDefault("margem_pct", "?")}%, averbadora {details.GetValueOrDefault("averbadora", "?")}."
                : $"{place}, {uf}. Roteiro completo ainda não lido: só o nome do arquivo no portal do {bank}.",
            Keywords = string.Join(", ", new[] { kind, city, uf, "consignado", "convênio", details.GetValueOrDefault("averbadora", "") }.Where(k => k.Length > 0)),
            DetailsJson = JsonSerializer.Serialize(new SortedDictionary<string, string>(details, StringComparer.Ordinal)),
            AiInstructions = read
                ? "Roteiro Simplificado lido do portal do banco. Siga as regras como estão; para o passo a passo de digitação, use o roteiro completo do portal."
                : "Só o nome do arquivo é conhecido. Não invente prazo, margem nem regra: indique o arquivo no portal do banco e a confirmação com a supervisão.",
            CreatedAt = now, UpdatedAt = now, UpdatedByName = "Sistema", Source = "sistema",
        };
    }
}
