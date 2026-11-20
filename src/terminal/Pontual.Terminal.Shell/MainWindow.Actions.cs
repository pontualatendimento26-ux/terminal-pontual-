using System.Globalization;
using System.Text;
using System.Text.Json;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

// ------------------------------------------------------------- a assistente abre coisas no Terminal
// A atendente pede no chat ("abre o WhatsApp", "pesquisa no Google a tabela do INSS") e a IA devolve a ação.
// O Terminal só abre o que já está liberado na configuração dele, pelo nome, e passa pela mesma política de
// navegação de sempre. A IA não escolhe endereço, não clica, não digita e não fecha nada.
public partial class MainWindow
{
    /// <summary>Os sites e programas que a assistente pode abrir neste Terminal (só nome e tipo).</summary>
    private object[] AssistantApps() =>
        _config is null ? [] : [.. _config.Items.Select(i => new { name = i.Name, kind = i.IsProgram ? ConfigItem.KindProgram : ConfigItem.KindSite })];

    private void RunAssistantAction(string requestId, string action, string value)
    {
        var (ok, message) = action switch
        {
            "open" => OpenByName(value),
            "search" => SearchGoogle(value),
            _ => (false, "Ação desconhecida."),
        };

        Log.Info($"assistente: {action} \"{value}\" → {(ok ? "ok" : message)}");
        try
        {
            _panel?.View.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "assistant-action-result", id = requestId, ok, message }, Json));
        }
        catch (Exception ex)
        {
            Log.Error("resposta da ação da assistente", ex);
        }
    }

    private (bool Ok, string Message) OpenByName(string name)
    {
        if (FindItem(name) is not { } item)
        {
            return (false, $"\"{name}\" não está liberado neste Terminal. O TI pode liberar na Central.");
        }

        OpenItem(item.Id);
        return (true, $"Abri {item.Name}.");
    }

    private (bool Ok, string Message) SearchGoogle(string query)
    {
        var google = _config?.Items.FirstOrDefault(i => !i.IsProgram && Host(i.Target).Contains("google.", StringComparison.OrdinalIgnoreCase));
        if (google is null)
        {
            return (false, "O Google não está liberado neste Terminal. O TI pode liberar na Central.");
        }

        var address = "https://www.google.com/search?q=" + Uri.EscapeDataString(query.Trim());
        if (_policy.Evaluate(address).Verdict != NavigationVerdict.Allowed)
        {
            return (false, "A pesquisa no Google não está liberada neste Terminal. O TI pode liberar na Central.");
        }

        if (_tabs.FirstOrDefault(t => t.Id == google.Id && !t.IsClosed) is { } tab)
        {
            ShowTab(tab, monitor: null);
            tab.View.CoreWebView2?.Navigate(address);
        }
        else
        {
            // Abre a aba do Google já na pesquisa; a navegação passa pela política como qualquer outra.
            _ = OpenSiteAsync(google with { Target = address });
        }

        return (true, $"Pesquisei no Google: {query.Trim()}");
    }

    /// <summary>O item de nome igual (sem acento nem maiúscula) ou, se só um contém o nome pedido, esse.</summary>
    private ConfigItem? FindItem(string name)
    {
        var wanted = Fold(name);
        if (_config is null || wanted.Length == 0)
        {
            return null;
        }

        var items = _config.Items;
        if (items.FirstOrDefault(i => Fold(i.Name) == wanted) is { } exact)
        {
            return exact;
        }

        var close = items.Where(i => Fold(i.Name).Contains(wanted, StringComparison.Ordinal) || wanted.Contains(Fold(i.Name), StringComparison.Ordinal)).ToList();
        return close.Count == 1 ? close[0] : null;
    }

    private static string Fold(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static string Host(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : "";
}
