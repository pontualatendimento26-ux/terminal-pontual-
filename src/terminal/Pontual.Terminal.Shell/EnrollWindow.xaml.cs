using System.IO;
using System.Windows;
using System.Windows.Automation;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Primeira execução: escolhe entre usar sem servidor (teste) e cadastrar a máquina num servidor
/// com <c>POST /device/enroll</c>. A escolha é gravada pelo <see cref="EnrollmentStore"/>.
/// </summary>
public partial class EnrollWindow : Window
{
    private readonly EnrollmentStore _store;

    internal EnrollWindow(EnrollmentStore store, string? serverUrl)
    {
        _store = store;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height; // em tela baixa, o conteúdo rola
        HostnameRun.Text = Environment.MachineName;
        ServerBox.Text = serverUrl ?? "";
        Loaded += (_, _) => LocalButton.Focus();
    }

    /// <summary>A escolha foi feita e gravada.</summary>
    internal event Action<Enrollment>? Completed;

    private void OnUseLocal(object sender, RoutedEventArgs e)
    {
        if (TrySave(Enrollment.Local))
        {
            Completed?.Invoke(Enrollment.Local);
        }
    }

    private async void OnEnroll(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowError(null);
            if (!EnrollmentStore.TryNormalizeServerUrl(ServerBox.Text, out var serverUrl))
            {
                ShowError("Informe o endereço do servidor, como http://192.168.0.10:5080.");
                ServerBox.Focus();
                return;
            }

            var code = NormalizeCode(CodeBox.Text);
            if (code.Length == 0)
            {
                ShowError("Informe o código de cadastro que o TI gerou na Central.");
                CodeBox.Focus();
                return;
            }

            SetBusy(true);
            Enrollment enrollment;
            try
            {
                using var api = new ServerApi(serverUrl, token: null);
                var response = await api.EnrollAsync(code, Environment.MachineName, WindowsInfo.Describe());
                enrollment = Enrollment.ForServer(serverUrl, response.DeviceId, response.Token);
            }
            catch (ServerApiException ex)
            {
                ShowError(ex.Message);
                return;
            }
            catch (Exception ex) when (ServerApi.IsNetworkFailure(ex))
            {
                ShowError("Não foi possível falar com o servidor. Confira o endereço e se este computador está na rede.");
                return;
            }
            finally
            {
                SetBusy(false);
            }

            if (TrySave(enrollment))
            {
                Completed?.Invoke(enrollment);
            }
        }
        catch (Exception ex)
        {
            // Manipulador "async void": nada pode escapar daqui.
            Log.Error("cadastro", ex);
            SetBusy(false);
            ShowError("O cadastro falhou por um erro inesperado. Tente de novo.");
        }
    }

    private bool TrySave(Enrollment enrollment)
    {
        try
        {
            _store.Save(enrollment);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Log.Error("gravar cadastro", ex);
            ShowError($"Não foi possível gravar a configuração em {_store.FilePath}.");
            return false;
        }
    }

    /// <summary>Aceita o código com ou sem hífen e com espaços: "abcd2345" vira "ABCD-2345".</summary>
    private static string NormalizeCode(string text)
    {
        var letters = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return letters.Length == 8 ? $"{letters[..4]}-{letters[4..]}" : text.Trim().ToUpperInvariant();
    }

    private void SetBusy(bool busy)
    {
        EnrollButton.IsEnabled = !busy;
        LocalButton.IsEnabled = !busy;
        ServerBox.IsEnabled = !busy;
        CodeBox.IsEnabled = !busy;
        EnrollButton.Content = busy ? "Conectando..." : "Conectar e cadastrar";
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (message is not null)
        {
            // Para o leitor de tela anunciar o erro.
            AutomationProperties.SetLiveSetting(ErrorText, AutomationLiveSetting.Assertive);
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(ErrorText)
                ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }
    }
}
