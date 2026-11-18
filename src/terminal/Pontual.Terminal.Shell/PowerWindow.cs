using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pontual.Terminal.Shell;

/// <summary>
/// Modo bloqueado: sem menu Iniciar, é por aqui que a atendente desliga, reinicia ou sai da conta.
/// As ações usam o <c>shutdown.exe</c> do Windows, iniciado direto (sem passar pelo shell).
/// </summary>
internal sealed class PowerWindow : Window
{
    private PowerWindow()
    {
        Title = "Terminal Pontual";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        FontFamily = (FontFamily)Application.Current.FindResource("FonteMarca");
        FontSize = 15;

        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        panel.Children.Add(new TextBlock
        {
            Text = "O que você quer fazer?",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("Tinta"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "As páginas abertas serão fechadas.",
            Foreground = (Brush)Application.Current.FindResource("Suave"),
            Margin = new Thickness(0, 0, 0, 16),
        });
        panel.Children.Add(Option("Desligar o computador", "BotaoPilula", "/s /t 0"));
        panel.Children.Add(Option("Reiniciar o computador", "BotaoClaro", "/r /t 0"));
        panel.Children.Add(Option("Sair da conta", "BotaoClaro", "/l"));

        var cancel = new Button
        {
            Content = "Cancelar",
            Style = (Style)Application.Current.FindResource("BotaoClaro"),
            IsCancel = true,
            Margin = new Thickness(0, 14, 0, 0),
        };
        panel.Children.Add(cancel);
        Content = panel;
    }

    public static void ShowFor(Window owner)
    {
        new PowerWindow { Owner = owner }.ShowDialog();
    }

    private Button Option(string label, string style, string arguments)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)Application.Current.FindResource(style),
            Margin = new Thickness(0, 0, 0, 8),
        };
        button.Click += (_, _) =>
        {
            Run(arguments);
            Close();
        };
        return button;
    }

    /// <summary>Sai da conta do Windows e volta à tela de entrada.</summary>
    internal static void SignOut() => Run("/l");

    private static void Run(string arguments)
    {
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("desligar", ex);
        }
    }
}
