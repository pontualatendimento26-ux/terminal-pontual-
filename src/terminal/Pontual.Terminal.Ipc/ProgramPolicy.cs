namespace Pontual.Terminal.Ipc;

/// <summary>
/// Regras para iniciar um programa instalado. O servidor já recusa estes caminhos (docs/V1-CONTRATO.md,
/// "Sites e programas"); o terminal confere de novo, para não depender só do servidor.
/// </summary>
public static class ProgramPolicy
{
    /// <summary>Nomes de arquivo (sem a extensão) que nunca são iniciados.</summary>
    public static readonly IReadOnlySet<string> RefusedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "regedit", "rundll32", "msiexec", "bash", "wsl", "wt",
    };

    /// <summary>Verdadeiro se o caminho não termina em <c>.exe</c> ou se o arquivo é um dos recusados.</summary>
    public static bool IsRefused(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        // O Windows ignora espaços e pontos no fim do nome: "cmd.exe." abre o cmd.exe.
        var clean = path.Trim().TrimEnd(' ', '.');
        if (clean.Length != path.Length || !clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Dois-pontos só vale na letra da unidade ("C:\..."); depois disso seria um fluxo alternativo de arquivo.
        if (clean.LastIndexOf(':') > 1)
        {
            return true;
        }

        var start = clean.LastIndexOfAny(['\\', '/', ':']) + 1;
        var name = clean[start..^4].Trim();
        return name.Length == 0 || RefusedNames.Contains(name);
    }

    /// <summary>
    /// O programa só inicia se o caminho for exatamente o de um item <c>program</c> da configuração recebida,
    /// não for recusado e for um caminho completo (sem busca no PATH nem na pasta atual).
    /// </summary>
    public static bool CanStart(string? path, TerminalConfig? config)
    {
        if (config is null || path is null || IsRefused(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        return config.Items.Any(i => i.IsProgram && string.Equals(i.Target, path, StringComparison.OrdinalIgnoreCase));
    }
}
