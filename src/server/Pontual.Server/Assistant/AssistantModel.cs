using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Pontual.Server.Assistant;

public sealed record AssistantTurn(string Role, string Text);

/// <summary>
/// Um pedido à IA. <see cref="SharedInstructions"/> é igual para todas as atendentes (regras + base de
/// conhecimento) e por isso vai primeiro e fica em cache; <see cref="PersonalInstructions"/> é o perfil de quem
/// pergunta. Com <see cref="Tools"/>, a IA consulta a base antes de responder (modo índice) e
/// <see cref="OnStatus"/> recebe a frase do que ela está consultando.
/// <see cref="ScreenText"/> e <see cref="ScreenImage"/> (JPEG) são o que estava na tela da atendente na hora
/// da pergunta; acompanham só a última fala e não entram no cache nem no histórico.
/// </summary>
public sealed record AssistantRequest(
    string ApiKey,
    string Model,
    string SharedInstructions,
    string PersonalInstructions,
    IReadOnlyList<AssistantTurn> Turns,
    IAssistantTools? Tools = null,
    Func<string, Task>? OnStatus = null,
    string? ScreenText = null,
    byte[]? ScreenImage = null);

/// <summary>Um pedido cuja resposta é um JSON no formato de <see cref="Schema"/> (análise das conversas).</summary>
public sealed record AssistantJsonRequest(string ApiKey, string Model, string Instructions, string Input, Dictionary<string, JsonElement> Schema);

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

    /// <summary>Devolve o JSON pedido, já no formato do esquema.</summary>
    Task<string> AskJsonAsync(AssistantJsonRequest request, CancellationToken ct);
}

/// <summary>Claude, pela API da Anthropic.</summary>
public sealed class ClaudeAssistantModel(ILogger<ClaudeAssistantModel> logger) : IAssistantModel
{
    /// <summary>Teto de idas e voltas com a base numa pergunta; as ferramentas têm o próprio teto de leitura.</summary>
    private const int MaxRounds = 8;

    private readonly Lock _gate = new();
    private string? _clientKey;
    private AnthropicClient? _client;

    public Task<string?> AskAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct) =>
        GuardAsync(() => request.Tools is null ? StreamAsync(request, onText, ct) : LoopAsync(request, request.Tools, onText, ct));

    public Task<string> AskJsonAsync(AssistantJsonRequest request, CancellationToken ct) => GuardAsync(async () =>
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = 16000,
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = request.Schema } },
            System = new List<TextBlockParam> { new() { Text = request.Instructions } },
            Messages = [new UserMessageParam { Content = request.Input }],
        };

        var response = await ClientFor(request.ApiKey).Messages.Create(parameters, cancellationToken: ct);
        var stop = response.StopReason?.Raw();
        if (stop == AssistantStop.Refusal)
        {
            throw new AssistantFailure("A IA não pôde fazer esta análise.");
        }

        if (stop == AssistantStop.MaxTokens)
        {
            throw new AssistantFailure("A análise ficou longa demais e foi cortada. Tente de novo.");
        }

        var json = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
            {
                json.Append(text.Text);
            }
        }

        return json.ToString();
    });

    private List<TextBlockParam> SystemOf(AssistantRequest request) =>
    [
        // Tudo até aqui é idêntico entre atendentes e entre perguntas: fica em cache e custa uma fração.
        new() { Text = request.SharedInstructions, CacheControl = new CacheControlEphemeral() },
        new() { Text = request.PersonalInstructions },
    ];

    private static List<MessageParam> MessagesOf(AssistantRequest request)
    {
        var messages = new List<MessageParam>(request.Turns.Count);
        var withScreen = request.ScreenText is { Length: > 0 } || request.ScreenImage is not null;
        for (var i = 0; i < request.Turns.Count; i++)
        {
            var turn = request.Turns[i];
            if (turn.Role == "assistant")
            {
                messages.Add(new AssistantMessageParam { Content = turn.Text });
            }
            else if (withScreen && i == request.Turns.Count - 1)
            {
                // A tela vem antes da pergunta: primeiro a foto, depois o texto lido da tela, por fim o que a atendente escreveu.
                var blocks = new List<ContentBlockParam>();
                if (request.ScreenImage is { } image)
                {
                    blocks.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(image), MediaType = MediaType.ImageJpeg } });
                }

                if (request.ScreenText is { Length: > 0 } screen)
                {
                    blocks.Add(new TextBlockParam { Text = screen });
                }

                blocks.Add(new TextBlockParam { Text = turn.Text });
                messages.Add(new UserMessageParam { Content = blocks });
            }
            else
            {
                messages.Add(new UserMessageParam { Content = turn.Text });
            }
        }

        return messages;
    }

    /// <summary>A base inteira já está nas instruções: a resposta chega em fluxo.</summary>
    private async Task<string?> StreamAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = 16000,
            // Pergunta e resposta em cima de um roteiro: pouco raciocínio basta e a resposta sai mais rápido.
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            System = SystemOf(request),
            Messages = MessagesOf(request),
        };

        string? stop = null;
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

        return stop;
    }

    /// <summary>
    /// Modo índice: a IA pede os conhecimentos de que precisa, o servidor os entrega e ela responde. Só o texto
    /// da resposta final vai para a atendente; enquanto isso ela vê o que está sendo consultado.
    /// </summary>
    private async Task<string?> LoopAsync(AssistantRequest request, IAssistantTools tools, Func<string, Task> onText, CancellationToken ct)
    {
        var client = ClientFor(request.ApiKey);
        var messages = MessagesOf(request);
        var definitions = new List<ToolUnion>();
        foreach (var tool in tools.Definitions)
        {
            definitions.Add(new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = new() { Properties = tool.Properties, Required = tool.Required },
            });
        }

        for (var round = 1; ; round++)
        {
            var parameters = new MessageCreateParams
            {
                Model = request.Model,
                MaxTokens = 16000,
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                System = SystemOf(request),
                Tools = definitions,
                Messages = messages,
            };

            var response = await client.Messages.Create(parameters, cancellationToken: ct);
            var stop = response.StopReason?.Raw();

            // Não há atalho para devolver a resposta como entrada: cada bloco é remontado no formato de envio.
            var echo = new List<ContentBlockParam>();
            var results = new List<ContentBlockParam>();
            var answer = new StringBuilder();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text))
                {
                    echo.Add(new TextBlockParam { Text = text.Text });
                    answer.Append(text.Text);
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out var use))
                {
                    echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    var (result, status) = tools.Run(use.Name, use.Input);
                    if (status.Length > 0 && request.OnStatus is { } onStatus)
                    {
                        await onStatus(status);
                    }

                    results.Add(new ToolResultBlockParam { ToolUseID = use.ID, Content = result });
                }
            }

            if (results.Count == 0 || stop is AssistantStop.Refusal or AssistantStop.MaxTokens)
            {
                if (answer.Length > 0)
                {
                    await onText(answer.ToString());
                }

                return stop;
            }

            if (round >= MaxRounds)
            {
                logger.LogWarning("A IA passou de {Rounds} consultas à base numa pergunta; a resposta foi encerrada.", MaxRounds);
                return stop;
            }

            messages.Add(new AssistantMessageParam { Content = echo });
            messages.Add(new UserMessageParam { Content = results });
        }
    }

    private async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
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
