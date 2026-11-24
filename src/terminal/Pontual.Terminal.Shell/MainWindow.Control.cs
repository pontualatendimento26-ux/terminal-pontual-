using System.Windows;
using Pontual.Terminal.Ipc;

namespace Pontual.Terminal.Shell;

/// <summary>
/// O lado do Terminal do controle remoto do TI: enquanto o TI está com a tela aberta, busca na Central
/// os comandos do mouse e do teclado e os aplica no Windows (<see cref="RemoteControl"/>), mostrando a
/// plaquinha discreta <see cref="ControlNotice"/> o tempo todo em que está sendo controlado.
/// Nada é gravado aqui. Enquanto ninguém está vendo a tela, o laço só cochila.
/// </summary>
public partial class MainWindow
{
    /// <summary>Quanto o laço espera antes de perguntar de novo enquanto ninguém está vendo a tela.</summary>
    private static readonly TimeSpan ControlIdle = TimeSpan.FromSeconds(2);

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        var api = _api!;
        while (!ct.IsCancellationRequested)
        {
            var wait = ControlIdle;
            try
            {
                // Só busca comandos quando o TI está com a tela aberta: fora disso, não há o que controlar.
                if (_screenWanted && _state != HeartbeatResponse.StatePending && _online != false)
                {
                    var batch = await api.FetchControlAsync(ct);
                    if (batch.Controlling)
                    {
                        ShowControlNotice();
                        if (batch.Events.Count > 0)
                        {
                            RemoteControl.Apply(batch.Events);
                        }

                        // Controle ativo: volta a buscar na hora (a própria chamada já segura alguns segundos).
                        wait = TimeSpan.Zero;
                    }
                    else
                    {
                        HideControlNotice();
                    }
                }
                else
                {
                    HideControlNotice();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Tempo-limite da espera longa sem comando novo: normal, pergunta de novo.
                wait = TimeSpan.Zero;
            }
            catch (ServerApiException ex)
            {
                // Servidor antigo (sem esta rota) ou máquina removida: tenta bem mais tarde.
                if (ex.Status == System.Net.HttpStatusCode.NotFound)
                {
                    wait = TimeSpan.FromMinutes(1);
                }
            }
            catch (Exception ex) when (ServerApi.IsNetworkFailure(ex))
            {
                // Sem conexão: o ciclo principal já mostra isso.
            }
            catch (Exception ex)
            {
                Log.Error("controle", ex);
                wait = TimeSpan.FromSeconds(10);
            }

            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        HideControlNotice();
    }

    private void ShowControlNotice()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_controlNotice is not null)
            {
                return;
            }

            try
            {
                _controlNotice = new ControlNotice("");
                _controlNotice.Show();
            }
            catch (Exception ex)
            {
                Log.Error("plaquinha-controle", ex);
            }
        });
    }

    private void HideControlNotice()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_controlNotice is null)
            {
                return;
            }

            try
            {
                _controlNotice.ForceClose();
            }
            catch (Exception ex)
            {
                Log.Error("plaquinha-controle", ex);
            }
            finally
            {
                _controlNotice = null;
            }
        });
    }
}
