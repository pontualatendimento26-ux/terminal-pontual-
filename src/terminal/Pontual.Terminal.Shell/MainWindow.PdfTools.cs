using System.Diagnostics;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Ferramentas de PDF do Terminal (pedido do dono, 20/11/2026): ler PDF (inclusive escaneado, com o OCR do Windows),
/// compactar e converter (JPG → PDF, PDF → imagem…). Quem faz o trabalho é o Pontual Tools, instalado em cada PC
/// por <c>Instalar-PontualTools.ps1</c> e rodando só nesta máquina em <c>http://127.0.0.1:8787</c>. Esta janela
/// apenas mostra a interface dele; os arquivos nunca saem do computador. Downloads caem na pasta Downloads como os demais.
/// </summary>
public partial class MainWindow
{
    private const string PdfToolsUrl = "http://127.0.0.1:8787/";
    private const string PdfToolsTask = "Pontual Tools";
    private static readonly HttpClient PdfToolsHttp = new() { Timeout = TimeSpan.FromSeconds(2) };

    private CalculatorWindow? _pdfTools;
    private bool _pdfToolsOpening;

    private async Task ShowPdfToolsAsync()
    {
        if (_environment is null)
        {
            ShowToast("O Terminal ainda está carregando. Tente o PDF de novo em alguns segundos.");
            return;
        }

        if (_pdfTools is not null)
        {
            if (!_pdfTools.IsVisible)
            {
                _pdfTools.Show();
            }

            if (_pdfTools.WindowState == System.Windows.WindowState.Minimized)
            {
                _pdfTools.WindowState = System.Windows.WindowState.Normal;
            }

            _pdfTools.Activate();
            return;
        }

        if (_pdfToolsOpening)
        {
            return; // já está abrindo (clique repetido)
        }

        if (!await PdfToolsRunningAsync())
        {
            // Pode ter parado: tenta ligar a tarefa instalada e espera alguns segundos.
            // Avisa na hora, senão a atendente acha que o botão não funciona (caso Kamila 2, 23/11/2026).
            _pdfToolsOpening = true;
            try
            {
                ShowToast("Abrindo as ferramentas de PDF… aguarde alguns segundos.");
                StartPdfToolsTask();
                var ok = false;
                for (var i = 0; i < 15 && !ok; i++)
                {
                    await Task.Delay(1000);
                    ok = await PdfToolsRunningAsync();
                }

                if (!ok)
                {
                    Log.Error("abrir as ferramentas de PDF", new InvalidOperationException("Pontual Tools não respondeu em 127.0.0.1:8787"));
                    ShowToast("As ferramentas de PDF não estão instaladas neste computador. Chame o TI para instalar o Pontual Tools.");
                    return;
                }
            }
            finally
            {
                _pdfToolsOpening = false;
            }
        }

        _pdfTools = new CalculatorWindow { Owner = this, Title = "Ferramentas de PDF", Width = 980, Height = 760, MinWidth = 600, MinHeight = 480 };
        _pdfTools.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        _pdfTools.Show();

        try
        {
            await _pdfTools.View.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or ArgumentException)
        {
            Log.Error("abrir as ferramentas de PDF", ex);
            _pdfTools.ForceClose();
            _pdfTools = null;
            ShowToast("Não foi possível abrir as ferramentas de PDF. Chame o TI.");
            return;
        }

        var core = _pdfTools.View.CoreWebView2;
        ApplySettings(core, isHome: false);
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationStarting += (_, args) =>
        {
            // A janela nunca sai do Pontual Tools local (nada da internet).
            if (!args.Uri.StartsWith(PdfToolsUrl, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };
        core.Navigate(PdfToolsUrl);
        _pdfTools.View.Focus();
    }

    private static async Task<bool> PdfToolsRunningAsync()
    {
        try
        {
            using var resp = await PdfToolsHttp.GetAsync(PdfToolsUrl + "api/health");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static void StartPdfToolsTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Run /TN \"{PdfToolsTask}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("ligar o Pontual Tools", ex);
        }
    }

    private void ClosePdfTools()
    {
        _pdfTools?.ForceClose();
        _pdfTools = null;
    }
}
