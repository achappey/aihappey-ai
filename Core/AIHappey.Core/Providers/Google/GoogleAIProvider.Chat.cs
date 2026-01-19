using ModelContextProtocol.Protocol;
using AIHappey.Common.Model;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Google;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
    : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var googleAI = GetClient();
        var metadata = request.GetProviderMetadata<GoogleProviderMetadata>(GetIdentifier());

        Mscc.GenerativeAI.Tool tool = new()
        {
            GoogleSearch = metadata?.GoogleSearch != null ?
                new Mscc.GenerativeAI.GoogleSearch()
                {
                    TimeRangeFilter = metadata?.GoogleSearch.TimeRangeFilter?.IsValid() == true
                        ? metadata?.GoogleSearch.TimeRangeFilter : null,
                }
                 : null,
            GoogleMaps = metadata?.GoogleMaps,
            CodeExecution = metadata?.CodeExecution,
            UrlContext = metadata?.UrlContext,
            FunctionDeclarations = request.Tools?.ToFunctionDeclarations().ToList()
        };

        List<Mscc.GenerativeAI.ContentResponse> inputItems =
                 [.. request.Messages
                .Where(a => a.Role != Common.Model.Role.system)
                .SkipLast(1)
                .Select(a => a.ToContentResponse())];

        var systemPrompt = string.Join("\n\n", request
            .Messages
            .Where(a => a.Role == Common.Model.Role.system)
            .SelectMany(a => a.Parts
                .OfType<TextUIPart>()
                .Select(y => y.Text)));

        Mscc.GenerativeAI.ChatSession chat = googleAI.ToChatSession(request.ToGenerationConfig(metadata),
            request.Model!, systemPrompt, inputItems);

        List<Mscc.GenerativeAI.Part> messageParts = request.Messages?.LastOrDefault()?.Parts
                .SelectMany(e => e.ToParts())
                .ToList() ?? [];

        bool messageOpen = false;
        string? currentToolCallId = null;

        await foreach (var update in chat.SendMessageStream(messageParts,
            tools: [tool],
            toolConfig: metadata?.ToolConfig,
            cancellationToken: cancellationToken)
            .WithCancellation(cancellationToken))
        {
            var candidate = update.Candidates?.FirstOrDefault();

            foreach (var part in candidate?.Content?.Parts ?? [])
            {
                if (part?.ExecutableCode != null)
                {
                    currentToolCallId = Guid.NewGuid().ToString();

                    yield return ToolCallPart.CreateProviderExecuted(currentToolCallId,
                         "code_execution",
                        JsonSerializer.Serialize(part?.ExecutableCode.Code, JsonSerializerOptions.Web));
                }
                else if (part?.CodeExecutionResult != null)
                {
                    var outcome = part?.CodeExecutionResult.Output ?? part?.CodeExecutionResult.Outcome.ToString() ?? string.Empty;
                    CallToolResult result = new()
                    {
                        IsError = part?.CodeExecutionResult.Outcome != Mscc.GenerativeAI.Outcome.OutcomeOk,
                        Content = [outcome.ToTextContentBlock()]
                    };

                    yield return new ToolOutputAvailablePart
                    {
                        ToolCallId = currentToolCallId!,
                        Output = result,
                        ProviderExecuted = true
                    };
                }
                else if (part?.FunctionCall != null)
                {
                    currentToolCallId = Guid.NewGuid().ToString();

                    yield return new ToolCallPart
                    {
                        ToolCallId = currentToolCallId,
                        ToolName = part.FunctionCall.Name,
                        Input = part.FunctionCall.Args!,
                        ProviderExecuted = false
                    };
                }
                else if (part?.Thought == true)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        yield return new ReasoningStartUIPart { Id = update.ResponseId! };

                        yield return new ReasoningDeltaUIPart()
                        {
                            Delta = part.Text,
                            Id = update.ResponseId!
                        };

                        var toughtSignature = part.ThoughtSignature != null ? Convert.ToBase64String(part.ThoughtSignature) : null;

                        yield return new ReasoningEndUIPart
                        {
                            Id = update.ResponseId!,
                            ProviderMetadata = toughtSignature != null
                                    ? new Dictionary<string, object> { { "signature", toughtSignature } }
                                        .ToProviderMetadata()
                                    : null
                        };
                    }
                }
                else
                {
                    if (part?.InlineData != null)
                    {
                        yield return part.InlineData.ToFileUIPart();
                    }

                    if (!messageOpen && !string.IsNullOrEmpty(part?.Text))
                    {
                        messageOpen = true;
                        yield return update.ResponseId!.ToTextStartUIMessageStreamPart();
                    }

                    if (!string.IsNullOrEmpty(part?.Text))
                    {
                        yield return new TextDeltaUIMessageStreamPart()
                        {
                            Delta = part.Text,
                            Id = update.ResponseId!
                        };
                    }

                    var finished = update.Candidates?.FirstOrDefault(c =>
                        c.FinishReason != null);

                    if (finished != null)
                    {
                        if (messageOpen)
                        {
                            messageOpen = false;
                            yield return update.ResponseId!.ToTextEndUIMessageStreamPart();
                        }

                        foreach (var responseUpdate in update.ToStreamingResponseUpdate())
                        {
                            yield return responseUpdate;
                        }

                        yield return finished.FinishReason
                        .ToString()!
                        .ToLowerInvariant()
                        .ToFinishUIPart(
                            update.ModelVersion!,
                            update.UsageMetadata?.CandidatesTokenCount ?? 0,
                            (update.UsageMetadata?.ToolUsePromptTokenCount ?? 0)
                                + (update.UsageMetadata?.PromptTokenCount ?? 0),
                            update.UsageMetadata?.TotalTokenCount ?? 0,
                            reasoningTokens: update.UsageMetadata?.ThoughtsTokenCount ?? 0,
                            temperature: request.Temperature
                        );
                    }
                }
            }
        }
    }

}
