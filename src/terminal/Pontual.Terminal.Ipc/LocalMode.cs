using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pontual.Terminal.Ipc;

/// <summary>Resultado da leitura de <c>config-local.json</c>: a configuração, ou a explicação do que está errado.</summary>
public sealed record LocalConfigResult(TerminalConfig? Config, string? Error)
{
    public bool Ok => Config is not null;
}

/// <summary>
/// Modo local (sem servidor): a lista vem de <c>config-local.json</c>, ao lado do programa, no mesmo
/// formato de <c>GET /device/config</c>. O TI pode editar o arquivo no Bloco de Notas, então os erros
/// são explicados em português, com o item que precisa de conserto.
/// </summary>
public static class LocalConfigFile
{
    public const string FileName = "config-local.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LocalConfigResult Load(string path, string fallbackAttendantName)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Fail($"O arquivo {path} não foi encontrado.");
            }

            return Parse(File.ReadAllText(path), fallbackAttendantName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Não foi possível ler o arquivo {path}: {ex.Message}");
        }
    }

    public static LocalConfigResult Parse(string json, string fallbackAttendantName)
    {
        TerminalConfig? raw;
        try
        {
            raw = JsonSerializer.Deserialize<TerminalConfig>(json, Json);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (linha {line + 1})" : "";
            return Fail($"O arquivo não é um JSON válido{where}. Confira vírgulas, aspas e chaves.");
        }

        if (raw is null || raw.Items is null)
        {
            return Fail("O arquivo precisa ter a lista \"items\".");
        }

        var items = new List<ConfigItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var item in raw.Items)
        {
            position++;
            var label = string.IsNullOrWhiteSpace(item?.Name) ? $"Item {position}" : $"Item \"{item.Name}\"";
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Target))
            {
                return Fail($"{label}: \"name\" e \"target\" são obrigatórios.");
            }

            var id = string.IsNullOrWhiteSpace(item.Id) ? $"item-{position}" : item.Id.Trim();
            if (!ids.Add(id))
            {
                return Fail($"{label}: o \"id\" \"{id}\" está repetido.");
            }

            var target = item.Target.Trim();
            var hosts = (item.Hosts ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            string kind;
            if (string.Equals(item.Kind, ConfigItem.KindProgram, StringComparison.OrdinalIgnoreCase))
            {
                kind = ConfigItem.KindProgram;
                if (ProgramPolicy.IsRefused(target) || !Path.IsPathFullyQualified(target))
                {
                    return Fail($"{label}: \"target\" precisa ser o caminho completo de um .exe permitido.");
                }

                hosts.Clear();
            }
            else if (string.IsNullOrWhiteSpace(item.Kind) || string.Equals(item.Kind, ConfigItem.KindSite, StringComparison.OrdinalIgnoreCase))
            {
                kind = ConfigItem.KindSite;
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return Fail($"{label}: \"target\" precisa ser um endereço completo, começando com http:// ou https://.");
                }

                // O domínio do próprio endereço sempre entra, como faz o servidor.
                var own = HostPolicy.NormalizeHost(target);
                if (own is not null && !hosts.Any(h => HostPolicy.NormalizeHost(h) == own))
                {
                    hosts.Insert(0, own);
                }

                var bad = hosts.FirstOrDefault(h => HostPolicy.NormalizeHost(h) is null);
                if (bad is not null)
                {
                    return Fail($"{label}: \"{bad}\" em \"hosts\" não é um domínio válido.");
                }
            }
            else
            {
                return Fail($"{label}: \"kind\" precisa ser \"site\" ou \"program\".");
            }

            items.Add(new ConfigItem(id, item.Name.Trim(), kind, target, string.IsNullOrWhiteSpace(item.Group) ? "Pontual e ferramentas" : item.Group.Trim(), hosts));
        }

        var name = string.IsNullOrWhiteSpace(raw.AttendantName) ? fallbackAttendantName : raw.AttendantName.Trim();
        return new LocalConfigResult(new TerminalConfig(raw.Version, name, raw.ObservationMode, items), null);
    }

    private static LocalConfigResult Fail(string error) => new(null, error);
}

/// <summary>
/// Registro do modo local: uma linha por evento, com data e hora, em
/// <c>%LocalAppData%\Pontual\Terminal\registro.log</c>, para o TI conferir depois do teste.
/// </summary>
public sealed class LocalEventLog(string path)
{
    public const string FileName = "registro.log";

    private readonly object _gate = new();

    public string FilePath { get; } = path;

    public static string Format(DeviceEvent e)
    {
        var what = e.Type switch
        {
            DeviceEvent.SessionStarted => "Terminal iniciado",
            DeviceEvent.Opened => "Aberto",
            DeviceEvent.Closed => "Fechado",
            DeviceEvent.Blocked => "Bloqueado",
            DeviceEvent.Observed => "Observado (domínio fora da lista)",
            DeviceEvent.Help => "Chamado ao TI",
            DeviceEvent.ChromeVisit => "Chrome: site não operacional",
            DeviceEvent.ChromeStay => "Chrome: site não operacional continua aberto",
            _ => e.Type,
        };
        var line = new StringBuilder(e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ").Append(what);
        if (!string.IsNullOrWhiteSpace(e.Item))
        {
            line.Append("  ").Append(OneLine(e.Item));
        }

        if (!string.IsNullOrWhiteSpace(e.Detail))
        {
            line.Append("  ").Append(OneLine(e.Detail));
        }

        return line.ToString();
    }

    /// <summary>Nunca lança: falha ao gravar o registro não pode derrubar o terminal.</summary>
    public bool Append(DeviceEvent e)
    {
        try
        {
            lock (_gate)
            {
                if (Path.GetDirectoryName(FilePath) is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(FilePath, Format(e) + Environment.NewLine, Encoding.UTF8);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Um nome de site ou domínio não pode abrir uma linha falsa no registro.
    private static string OneLine(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
