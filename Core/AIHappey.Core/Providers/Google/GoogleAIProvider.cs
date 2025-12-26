using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using AIHappey.Common.Model;
using OAIC = OpenAI.Chat;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AIHappey.Core.AI;
using System.Net.Mime;
using System.Text;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model.Providers;
using AIHappey.Common.Extensions;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.Google;

public class GoogleAIProvider(IApiKeyResolver keyResolver, ILogger<GoogleAIProvider> logger)
    : IModelProvider
{
    private readonly string FILES_API = "https://generativelanguage.googleapis.com/v1beta/files";

    private Mscc.GenerativeAI.GoogleAI GetClient()
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Google)} API key.");

        return new(key, logger: logger);
    }

    private static readonly string Google = "Google";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var googleAI = GetClient();
        var generativeModel = googleAI.GenerativeModel();
        var models = await generativeModel.ListModels(pageSize: 1000);

        string[] excludedSubstrings = [
            "embedding",
            "native",
            "tts",
        ];

        return models
            .Select(a =>
            {
                var id = a.Name?.Split("/").LastOrDefault() ?? string.Empty;

                GoogleAIModels.ModelCreatedAt.TryGetValue(id, out var createdAt);

                return new Model()
                {
                    Name = a.DisplayName!,
                    //Publisher = Google,
                    OwnedBy = Google,
                    Id = id.ToModelId(GetIdentifier()),
                    Created = createdAt != default ? createdAt.ToUnixTimeSeconds() : null
                };
            })
            .Where(a => excludedSubstrings.All(z => a.Id?.Contains(z) != true));
    }

    public float? GetPriority() => 1;

    public async Task<UIMessagePart> CompleteAsync(ChatCompletionOptions request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }


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
                            (update.UsageMetadata?.ToolUsePromptTokenCount ?? 0)
                                + (update.UsageMetadata?.CandidatesTokenCount ?? 0),
                            update.UsageMetadata?.PromptTokenCount ?? 0,
                            update.UsageMetadata?.TotalTokenCount ?? 0,
                            reasoningTokens: update.UsageMetadata?.ThoughtsTokenCount ?? 0,
                            temperature: request.Temperature
                        );
                    }
                }
            }
        }
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();
        var googleAI = GetClient();

        List<Mscc.GenerativeAI.ContentResponse> inputItems = [.. chatRequest.Messages
            .SkipLast(1)
            .Select(a => a.ToContentResponse())];

        Mscc.GenerativeAI.GoogleSearch? googleSearch = chatRequest.Metadata.ToGoogleSearch();
        Mscc.GenerativeAI.CodeExecution? codeExecution = chatRequest.Metadata.UseCodeExecution() ? new() : null;
        Mscc.GenerativeAI.UrlContext? urlContext = chatRequest.Metadata.UseUrlContext() ? new() : null;
        Mscc.GenerativeAI.GoogleMaps? googleMaps = chatRequest.Metadata.UseGoogleMaps() ? new() : null;

        Mscc.GenerativeAI.Tool? tool = urlContext != null
            || googleSearch != null
            || googleMaps != null
            || codeExecution != null ? new()
            {
                GoogleSearch = googleSearch,
                UrlContext = urlContext,
                GoogleMaps = googleMaps,
                CodeExecution = codeExecution
            } : null;

        Mscc.GenerativeAI.ChatSession chat = googleAI.ToChatSession(chatRequest.ToGenerationConfig(), model!,
            chatRequest.SystemPrompt ?? string.Empty,
            inputItems);

        var text = chatRequest.Messages.LastOrDefault()?.ToText();

        List<Mscc.GenerativeAI.Part> parts = [new Mscc.GenerativeAI.Part() { Text =
            text }];

        var response = await chat.SendMessage(parts,
            tools: tool != null ? [tool] : null,
            cancellationToken: cancellationToken);

        List<ContentBlock> inlineDataBlocks = response.Candidates?.FirstOrDefault()?.Content?
            .Parts.Where(a => a.InlineData != null)?
            .Select(a => a.InlineData)
            .Select(a => a?.MimeType.StartsWith("image/") == true ? new ImageContentBlock()
            {
                MimeType = a?.MimeType!,
                Data = a?.Data!
            } : (ContentBlock)new EmbeddedResourceBlock()
            {
                Resource = a?.MimeType.StartsWith("text/") == true
                    || a?.MimeType.StartsWith(MediaTypeNames.Application.Json) == true
                    ? new TextResourceContents()
                    {
                        Text = Encoding.UTF8.GetString(Convert.FromBase64String(a?.Data!)),
                        MimeType = a?.MimeType,
                        Uri = FILES_API
                    } : new BlobResourceContents()
                    {
                        Blob = a?.Data!,
                        MimeType = a?.MimeType,
                        Uri = FILES_API
                    }
            })
            .ToList() ?? [];

        var textBlock = !string.IsNullOrEmpty(response.Text) ? response.Text?.ToTextContentBlock() : null;

        if (textBlock != null)
            inlineDataBlocks.Add(textBlock);

        ContentBlock resultBlock = inlineDataBlocks.OfType<EmbeddedResourceBlock>().Any() ?
                 inlineDataBlocks.OfType<EmbeddedResourceBlock>().First()
                 : string.Join(Environment.NewLine, inlineDataBlocks.OfType<TextContentBlock>().Select(a => a.Text)).ToTextContentBlock()
                 ?? throw new Exception("No content");

        return new CreateMessageResult()
        {
            Model = response.ModelVersion!,
            Content = [resultBlock],
            StopReason = response.Candidates?.FirstOrDefault()?.FinishReason.ToStopReason(),
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Meta = new System.Text.Json.Nodes.JsonObject()
            {
                ["inputTokens"] = response?.UsageMetadata?.PromptTokenCount,
                ["totalTokens"] = response?.UsageMetadata?.TotalTokenCount
            }
        };
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => GoogleExtensions.Identifier();

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
