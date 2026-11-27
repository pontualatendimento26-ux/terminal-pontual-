using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// PDFs dos sites (pedido do dono, 24/11/2026, caso Kamila): o Nexus gera o PDF da proposta na própria tela e o mostra
/// numa janela nova (<c>blob:</c> ou em branco), que o Terminal descartava sem avisar. Agora essa janela abre, com a
/// mesma política de endereços da aba; e o PDF baixado pelo Nexus abre sozinho assim que o download termina.
/// </summary>
public partial class MainWindow
{
    private const int MaxSitePopups = 6;
    private const string NexusServer = "192.168.0.171";
    private readonly List<Window> _sitePopups = [];

    private static bool IsNexus(SiteTab tab) =>
        tab.Name.Contains("nexus", StringComparison.OrdinalIgnoreCase)
        || Host(tab.Target).Contains("nexus", StringComparison.OrdinalIgnoreCase)
        || Host(tab.Target) == NexusServer;

    /// <summary>Documento gerado pela página de um domínio que a aba poderia abrir (<c>blob:https://site/...</c>).</summary>
    private bool IsAllowedBlob(string? uri)
    {
        if (uri is null || !uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var verdict = _policy.Evaluate(uri["blob:".Length..]).Verdict;
        return verdict == NavigationVerdict.Allowed || (verdict == NavigationVerdict.NotListed && _observationMode);
    }

    private static bool IsBlank(string? uri) =>
        string.IsNullOrEmpty(uri) || uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase);

    /// <summary>Janela nova pedida por um site: PDF gerado na página (ou em branco, só no Nexus) ganha uma janela própria.</summary>
    private async void OnSiteNewWindow(SiteTab tab, CoreWebView2 core, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!IsAllowedBlob(args.Uri) && !(IsNexus(tab) && IsBlank(args.Uri)))
        {
            // Os demais abrem na mesma aba e passam pela mesma política (em NavigationStarting).
            if (!IsBlank(args.Uri) && !args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    core.Navigate(args.Uri);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // Endereço que o navegador não aceita: fica onde está.
                }
            }

            return;
        }

        if (_sitePopups.Count >= MaxSitePopups)
        {
            ShowToast("Há muitas janelas de documento abertas. Feche algumas e tente de novo.");
            return;
        }

        var deferral = args.GetDeferral();
        Window? window = null;
        try
        {
            var view = new WebView2();
            window = ShowDocumentWindow(tab.Name, view);
            await view.EnsureCoreWebView2Async(_environment);
            var popup = view.CoreWebView2;
            var opened = window;
            var loaded = false;

            ApplySettings(popup, isHome: false, openPdfs: false);
            WireSiteKeys(view);
            popup.NavigationStarting += (_, e) =>
            {
                if (!IsAllowedBlob(e.Uri))
                {
                    OnSiteNavigationStarting(tab, e);
                }
                else if (IsNexus(tab))
                {
                    SaveBlobPdf(core, e.Uri);
                }
            };
            popup.NavigationCompleted += (_, e) => loaded |= e.IsSuccess && !IsBlank(popup.Source);
            popup.DownloadStarting += (_, e) =>
            {
                // A janela só serviu para baixar: some agora e fecha quando o arquivo terminar.
                if (loaded || e.Cancel)
                {
                    return;
                }

                opened.Hide();
                var operation = e.DownloadOperation;
                operation.StateChanged += (_, _) =>
                {
                    if (operation.State == CoreWebView2DownloadState.InProgress)
                    {
                        return;
                    }

                    var path = operation.ResultFilePath;
                    var show = operation.State == CoreWebView2DownloadState.Completed && IsNexus(tab) && IsPdf(path);
                    // Fora do evento: fechar a janela descarta o WebView2 que está avisando.
                    Dispatcher.BeginInvoke(() =>
                    {
                        opened.Close();
                        if (show)
                        {
                            _ = OpenPdfAsync(path);
                        }
                    });
                };
            };
            popup.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(opened.Close);
            popup.NewWindowRequested += (_, e) => OnSiteNewWindow(tab, popup, e);
            args.NewWindow = popup;
        }
        catch (Exception ex)
        {
            // Manipulador "async void": nada pode escapar daqui.
            Log.Error("abrir documento do site", ex);
            window?.Close();
            ShowToast("Não foi possível abrir o documento. Tente de novo ou chame o TI.");
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Nomes dos PDFs que já estão na tela: são salvos em Downloads sem abrir uma segunda janela.</summary>
    private readonly HashSet<string> _pdfsOnScreen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>O PDF que o Nexus mostra em janela nova também vai para Downloads, como um download comum (pedido do dono).</summary>
    private void SaveBlobPdf(CoreWebView2 core, string uri)
    {
        var name = $"Nexus {DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}.pdf";
        _pdfsOnScreen.Add(name);
        // Lê logo o conteúdo: a página costuma descartar o endereço blob em seguida. Só PDF é salvo.
        _ = core.ExecuteScriptAsync(
            "(u=>fetch(u).then(r=>r.blob()).then(b=>{if(b.type.split(';')[0]!=='application/pdf')return;" +
            "const a=document.createElement('a'),o=URL.createObjectURL(b);a.href=o;a.download=" + JsonSerializer.Serialize(name) +
            ";document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(o),60000)})" +
            ".catch(()=>{const a=document.createElement('a');a.href=u;a.download=" + JsonSerializer.Serialize(name) +
            ";document.body.appendChild(a);a.click();a.remove()}))(" + JsonSerializer.Serialize(uri) + ")");
    }

    /// <summary>A página tentou trocar a própria aba pelo PDF: abre em janela separada, para não perder a tela do site.</summary>
    private static void OpenBlobInWindow(SiteTab tab, string uri)
    {
        _ = tab.View.CoreWebView2?.ExecuteScriptAsync("window.open(" + JsonSerializer.Serialize(uri) + ",'_blank')");
    }

    /// <summary>Se a atendente um dia negou "vários downloads", o navegador recusa calado para sempre: no Nexus fica sempre permitido.</summary>
    private static async Task AllowNexusDownloadsAsync(SiteTab tab)
    {
        if (!IsNexus(tab) || !Uri.TryCreate(tab.Target, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            await tab.View.CoreWebView2.Profile.SetPermissionStateAsync(
                CoreWebView2PermissionKind.MultipleAutomaticDownloads, uri.GetLeftPart(UriPartial.Authority), CoreWebView2PermissionState.Allow);
        }
        catch (Exception ex)
        {
            // WebView2 antigo: segue sem isto.
            Log.Error("permitir downloads do Nexus", ex);
        }
    }

    private static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mostra um PDF já salvo, sem depender do programa padrão do Windows.</summary>
    private async Task OpenPdfAsync(string path)
    {
        Window? window = null;
        try
        {
            var view = new WebView2();
            window = ShowDocumentWindow(Path.GetFileName(path), view);
            await view.EnsureCoreWebView2Async(_environment);
            var core = view.CoreWebView2;
            var address = new Uri(path).AbsoluteUri;
            ApplySettings(core, isHome: false, openPdfs: false);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
            {
                // Esta janela só mostra este arquivo.
                var same = Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.IsFile
                    && string.Equals(target.LocalPath, path, StringComparison.OrdinalIgnoreCase);
                e.Cancel = !same;
            };
            core.Navigate(address);
            view.Focus();
        }
        catch (Exception ex)
        {
            Log.Error("abrir PDF", ex);
            window?.Close();
            ShowToast("O PDF foi salvo na pasta Downloads, mas não abriu sozinho.");
        }
    }

    private Window ShowDocumentWindow(string title, WebView2 view)
    {
        var area = SystemParameters.WorkArea;
        var window = new Window
        {
            Owner = this,
            Title = title,
            Width = Math.Min(1000, area.Width * 0.9),
            Height = Math.Min(820, area.Height * 0.9),
            MinWidth = 500,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = System.Windows.Media.Brushes.White,
            Content = view,
        };
        window.Closed += (_, _) =>
        {
            _sitePopups.Remove(window);
            view.Dispose();
        };
        _sitePopups.Add(window);
        window.Show();
        window.Activate();
        return window;
    }
}
