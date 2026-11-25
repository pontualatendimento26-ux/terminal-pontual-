namespace Pontual.Terminal.Ipc;

/// <summary>Como uma aba fora da tela está descansando no modo leve.</summary>
public enum TabRest
{
    /// <summary>Funcionando normalmente.</summary>
    None,

    /// <summary>Continua rodando e conectada; o Windows só libera parte da memória dela (WhatsApp e CRM).</summary>
    LowMemory,

    /// <summary>Pausada: acorda quando volta para a tela.</summary>
    Asleep,
}

/// <summary>Regras do modo leve (computador fraco), sem interface, para poderem ser testadas.</summary>
public static class LightMode
{
    /// <summary>Tempo fora da tela até a aba descansar.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Aba que recebe mensagens e por isso nunca dorme: WhatsApp e CRM. O CRM vem pelo endereço cadastrado na Central;
    /// se ele faltar, vale o nome do item. Na dúvida fica ligada: perder mensagem é pior que gastar memória.
    /// </summary>
    public static bool StaysConnected(string name, string target, IEnumerable<string>? hosts)
    {
        if (target.Contains("whatsapp", StringComparison.OrdinalIgnoreCase)
            || name.Contains("whatsapp", StringComparison.OrdinalIgnoreCase)
            || name.Contains("crm", StringComparison.OrdinalIgnoreCase)
            || name.Contains("pontualconsig", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Host.Contains("crm", StringComparison.OrdinalIgnoreCase)
                || hosts?.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)) == true);
    }

    /// <summary>O descanso certo para uma aba, dado há quanto tempo saiu da tela (nulo = está na tela).</summary>
    public static TabRest RestFor(bool staysConnected, DateTime? hiddenSince, DateTime now) =>
        hiddenSince is not { } since || now - since < IdleAfter ? TabRest.None
        : staysConnected ? TabRest.LowMemory
        : TabRest.Asleep;
}
