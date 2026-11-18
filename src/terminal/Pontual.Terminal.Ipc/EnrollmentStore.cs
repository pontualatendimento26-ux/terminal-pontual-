using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pontual.Terminal.Ipc;

public enum TerminalMode
{
    /// <summary>Sem servidor: lista de <c>config-local.json</c> e registro em arquivo.</summary>
    Local,

    /// <summary>Cadastrado num servidor Pontual.</summary>
    Server,
}

/// <summary>A escolha feita na primeira execução. No modo local, os campos do servidor ficam vazios.</summary>
public sealed record Enrollment(TerminalMode Mode, string ServerUrl, string DeviceId, string Token)
{
    public static Enrollment Local { get; } = new(TerminalMode.Local, "", "", "");

    public static Enrollment ForServer(string serverUrl, string deviceId, string token) => new(TerminalMode.Server, serverUrl, deviceId, token);
}

public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    byte[] Unprotect(byte[] cipher);
}

/// <summary>DPAPI no escopo do usuário: só a mesma conta do Windows, na mesma máquina, lê o token.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Pontual.Terminal.v1");

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Guarda o cadastro local em <c>%LocalAppData%\Pontual\Terminal\cadastro.json</c>.
/// O token nunca vai para o disco em texto claro: é gravado protegido, em Base64.
/// </summary>
public sealed class EnrollmentStore(string directory, ISecretProtector protector)
{
    public const string FileName = "cadastro.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pontual", "Terminal");

    public string FilePath { get; } = Path.Combine(directory, FileName);

    /// <summary>Devolve <c>null</c> se não há cadastro ou se o arquivo não pôde ser lido (aí o programa pede o cadastro de novo).</summary>
    public Enrollment? Load()
    {
        try
        {
            return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(Enrollment enrollment)
    {
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, Serialize(enrollment));
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem conseguir apagar, regrava vazio seria pior; o próximo Save substitui o arquivo.
        }
    }

    public string Serialize(Enrollment enrollment)
    {
        if (enrollment.Mode == TerminalMode.Local)
        {
            return JsonSerializer.Serialize(new StoredEnrollment("local", null, null, null), Json);
        }

        var cipher = protector.Protect(Encoding.UTF8.GetBytes(enrollment.Token));
        return JsonSerializer.Serialize(
            new StoredEnrollment("server", enrollment.ServerUrl, enrollment.DeviceId, Convert.ToBase64String(cipher)), Json);
    }

    public Enrollment? Parse(string json)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredEnrollment>(json, Json);
            if (stored is null)
            {
                return null;
            }

            if (string.Equals(stored.Mode, "local", StringComparison.OrdinalIgnoreCase))
            {
                return Enrollment.Local;
            }

            if (!string.Equals(stored.Mode, "server", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(stored.DeviceId)
                || string.IsNullOrWhiteSpace(stored.ProtectedToken)
                || !TryNormalizeServerUrl(stored.ServerUrl, out var serverUrl))
            {
                return null;
            }

            var token = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(stored.ProtectedToken)));
            return token.Length == 0 ? null : Enrollment.ForServer(serverUrl, stored.DeviceId, token);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            // Arquivo corrompido, ou gravado por outra conta do Windows: vale como "sem cadastro".
            return null;
        }
    }

    /// <summary>
    /// Aceita "192.168.0.10:5080" ou "http://192.168.0.10:5080/" e devolve "http://192.168.0.10:5080".
    /// Só http e https, sem usuário e senha no endereço.
    /// </summary>
    public static bool TryNormalizeServerUrl(string? input, out string serverUrl)
    {
        serverUrl = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        serverUrl = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private sealed record StoredEnrollment(string? Mode, string? ServerUrl, string? DeviceId, string? ProtectedToken);
}
