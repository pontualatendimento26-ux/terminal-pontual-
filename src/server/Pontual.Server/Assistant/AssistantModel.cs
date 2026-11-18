using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Pontual.Server.Assistant;

public sealed record AssistantTurn(string Role, string Text);

/// <summary>
/// Um pedido à IA. <see cref="SharedInstructions"/> é igual para todas as atendentes (regras + roteiros) e
/// por isso vai primeiro e fica em cache; <see cref="PersonalInstructions"/> é o perfil de quem pergunta.
/// </summary>
public sealed record AssistantRequest(
    string ApiKey,
    string Model,
    string SharedInstructions,
    string PersonalInstructions,
    IReadOnlyList<AssistantTurn> Turns);

/// <summary>Falha prevista ao falar com a IA; a mensagem já está pronta para aparecer na tela.</summary>
public sealed class AssistantFailure(string message, Exception? inner = null) : Exception(message, inner);

public static class AssistantStop
{
    public const string Refusal = "refusal";
    public const string MaxTokens = "max_tokens";
}

public interface IAssistantModel
{
    /// <summary>
    /// Envia a conversa e entrega a resposta aos pedaços, conforme chega. Devolve o motivo da parada
    /// (ver <see cref="AssistantStop"/>), ou nulo se o serviço não informou.
    /// </summary>
    Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct);
}

/// <summary>Claude, pela API da Anthropic.</summary>
public sealed class ClaudeAssistantModel(ILogger<ClaudeAssistantModel> logger) : IAssistantModel
{
    private readonly Lock _gate = new();
    private string? _clientKey;
    private AnthropicClient? _client;

    public async Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct)
    {
        var messages = new List<MessageParam>(request.Turns.Count);
        foreach (var turn in request.Turns)
        {
            messages.Add(turn.Role == "assistant"
                ? new AssistantMessageParam { Content = turn.Text }
                : new UserMessageParam { Content = turn.Text });
        }

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = 16000,
            // Pergunta e resposta em cima de um roteiro: pouco raciocínio basta e a resposta sai mais rápido.
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            System = new List<TextBlockParam>
            {
                // Tudo até aqui é idêntico entre atendentes e entre perguntas: fica em cache e custa uma fração.
                new() { Text = request.SharedInstructions, CacheControl = new CacheControlEphemeral() },
                new() { Text = request.PersonalInstructions },
            },
            Messages = messages,
        };

        string? stop = null;
        try
        {
            await foreach (var streamEvent in ClientFor(request.ApiKey).Messages.CreateStreaming(parameters, cancellationToken: ct))
            {
                if (streamEvent.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var text))
                {
                    await onText(text.Text);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason)
                {
                    stop = reason.Raw();
                }
            }
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw Fail("A chave da IA foi recusada. Avise o TI para conferir a chave na Central.", ex);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw Fail("A conta da IA não tem permissão para este pedido. Avise o TI.", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw Fail("A assistente está com muitos pedidos agora. Tente de novo em um minuto.", ex);
        }
        catch (AnthropicNotFoundException ex)
        {
            throw Fail("O modelo de IA escolhido na Central não foi encontrado. Avise o TI.", ex);
        }
        catch (Anthropic4xxException ex)
        {
            // Inclui saldo insuficiente na conta da IA.
            throw Fail("O serviço da IA não aceitou o pedido. Avise o TI.", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw Fail("O serviço da IA está instável. Tente de novo em instantes.", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw Fail("Sem conexão com o serviço da IA. Confira a internet do servidor e tente de novo.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw Fail("A assistente não conseguiu responder. Tente de novo.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Fail("Sem conexão com o serviço da IA. Confira a internet do servidor e tente de novo.", ex);
        }

        return stop;
    }

    private AssistantFailure Fail(string message, Exception ex)
    {
        logger.LogWarning(ex, "Falha ao falar com a IA: {Message}", message);
        return new AssistantFailure(message, ex);
    }

    private AnthropicClient ClientFor(string apiKey)
    {
        lock (_gate)
        {
            if (_client is null || _clientKey != apiKey)
            {
                _client = new AnthropicClient { ApiKey = apiKey };
                _clientKey = apiKey;
            }

            return _client;
        }
    }
}
