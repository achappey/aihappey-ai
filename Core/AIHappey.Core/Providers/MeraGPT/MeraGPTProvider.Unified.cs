using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MeraGPT;

public sealed partial class MeraGPTProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ResponseResult> ResponsesAsyncCore(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var response = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        return response.ToResponseResult();
    }

    private async IAsyncEnumerable<AIHappey.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsyncCore(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = options.ToUnifiedRequest(GetIdentifier());
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    private async IAsyncEnumerable<UIMessagePart> StreamAsyncCore(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = chatRequest.ToUnifiedRequest(GetIdentifier());
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
        }
    }

    private async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var model = NormalizeModel(request.Model);
        if (model == "decider-1")
            throw new NotSupportedException("Decider 1 is available through the Decisions contract, not text generation.");
        if (model == "restyler-1")
            return await ExecuteRestyleAsync(request, cancellationToken);
        if (model == "fanout-1")
            return await ExecuteFanoutUnifiedAsync(request, cancellationToken);

        ApplyAuthHeader();
        var completion = await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
        return completion;
    }

    private async Task<AIResponse> ExecuteFanoutUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TopP is not null)
            throw new NotSupportedException("MeraGPT Fanout does not support top_p.");

        ApplyAuthHeader();
        var options = request.ToChatCompletionOptions(GetIdentifier());
        options.Stream = false;
        options.Store ??= false;
        var completion = await CompleteChatAsync(options, cancellationToken);
        var response = completion.ToUnifiedResponse(GetIdentifier());
        var nativeMetadata = completion.AdditionalProperties!["meragpt"].Clone();
        var responseMetadata = new Dictionary<string, object?>(response.Metadata ?? [], StringComparer.Ordinal)
        {
            ["meragpt"] = nativeMetadata
        };
        var outputMetadata = new Dictionary<string, object?>(response.Output?.Metadata ?? [], StringComparer.Ordinal)
        {
            ["meragpt"] = nativeMetadata
        };
        var outputItems = response.Output?.Items?.Select(item =>
        {
            var itemMetadata = new Dictionary<string, object?>(item.Metadata ?? [], StringComparer.Ordinal)
            {
                ["meragpt"] = nativeMetadata
            };
            return new AIOutputItem
            {
                Type = item.Type,
                Role = item.Role,
                Metadata = itemMetadata,
                Content = item.Content?.Select(content => content is AITextContentPart text
                    ? new AITextContentPart
                    {
                        Type = "text",
                        Text = text.Text,
                        Metadata = new Dictionary<string, object?>(text.Metadata ?? [], StringComparer.Ordinal)
                        {
                            ["meragpt"] = nativeMetadata
                        }
                    }
                    : content).ToList()
            };
        }).ToList();

        return new AIResponse
        {
            ProviderId = response.ProviderId,
            Model = response.Model,
            Status = response.Status,
            Usage = response.Usage,
            Metadata = responseMetadata,
            Output = response.Output is null
                ? null
                : new AIOutput { Items = outputItems, Metadata = outputMetadata }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizeModel(request.Model);
        if (model is "restyler-1" or "fanout-1")
        {
            var response = await ExecuteUnifiedAsync(request, cancellationToken);
            await foreach (var item in StreamBufferedResponseAsync(response, request, cancellationToken))
                yield return item;
            yield break;
        }

        if (model == "decider-1")
            throw new NotSupportedException("Decider 1 is available through the Decisions contract, not text generation.");

        ApplyAuthHeader();
        await foreach (var item in this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken))
            yield return item;
    }

    private async Task<AIResponse> ExecuteRestyleAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRestyleOptions(request);
        var text = GetRestyleText(request);
        if (text.Length > 6000)
            throw new ArgumentException($"MeraGPT Restyler accepts at most 6000 characters; received {text.Length}.", nameof(request));

        var body = new JsonObject
        {
            ["model"] = "restyler-1",
            ["text"] = text
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/restyle")
        {
            Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"MeraGPT Restyler failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var outputText = root.GetProperty("text").GetString()
                ?? throw new JsonException("Restyler response text was null.");
            var nativeUsage = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? usage
                : default;
            var normalizedUsage = nativeUsage.ValueKind == JsonValueKind.Object
                ? new AIUsage
                {
                    InputTokens = ReadUsageCount(nativeUsage, "input_tokens"),
                    OutputTokens = ReadUsageCount(nativeUsage, "output_tokens"),
                    TotalTokens = ReadUsageCount(nativeUsage, "total_tokens")
                }
                : null;
            var meragptMetadata = new Dictionary<string, object?> { ["response"] = root.Clone() };
            return new AIResponse
            {
                ProviderId = GetIdentifier(),
                Model = request.Model,
                Status = "completed",
                Output = new AIOutput
                {
                    Items =
                    [
                        new AIOutputItem
                        {
                            Type = "message",
                            Role = "assistant",
                            Content = [new AITextContentPart { Type = "text", Text = outputText }],
                            Metadata = meragptMetadata
                        }
                    ]
                },
                Usage = normalizedUsage,
                Metadata = new Dictionary<string, object?> { ["meragpt"] = root.Clone() }
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("MeraGPT returned an invalid Restyler response.", exception);
        }
    }

    private static void ValidateRestyleOptions(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Instructions)
            || request.Headers is { Count: > 0 }
            || request.Verbosity is not null
            || request.Temperature.HasValue && request.Temperature.Value != 1f
            || request.TopP is not null
            || request.MaxOutputTokens is not null
            || request.MaxToolCalls is not null
            || request.ParallelToolCalls is not null
            || request.ToolChoice is not null
            || request.ResponseFormat is not null
            || request.Tools is { Count: > 0 })
            throw new NotSupportedException("MeraGPT Restyler accepts only a text document and model.");
    }

    private static string GetRestyleText(AIRequest request)
    {
        var latestUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (latestUserMessage is not null)
        {
            var content = latestUserMessage.Content ?? [];
            if (content.Count == 0 || content.Any(part => part is not AITextContentPart))
                throw new ArgumentException("MeraGPT Restyler accepts text-only user input.", nameof(request));
            var userText = string.Concat(content.Cast<AITextContentPart>().Select(part => part.Text));
            if (!string.IsNullOrWhiteSpace(userText))
                return userText;
        }

        var inputText = request.Input?.Text;
        if (!string.IsNullOrWhiteSpace(inputText))
            return inputText;
        throw new ArgumentException("MeraGPT Restyler requires non-empty text in the latest user message.", nameof(request));
    }

    private static int? ReadUsageCount(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count)
            && count >= 0
                ? count
                : null;

    private async IAsyncEnumerable<AIStreamEvent> StreamBufferedResponseAsync(
        AIResponse response,
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in response.Output?.Items ?? [])
        {
            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var eventId = Guid.NewGuid().ToString("n");
                var timestamp = DateTimeOffset.UtcNow;
                var textMetadata = text.Metadata ?? item.Metadata;
                var providerMetadata = ToProviderMetadata(textMetadata);
                yield return CreateStreamEvent(
                    eventId,
                    "text-start",
                    new AITextStartEventData { ProviderMetadata = providerMetadata },
                    timestamp,
                    textMetadata);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreateStreamEvent(
                        eventId,
                        "text-delta",
                        new AITextDeltaEventData { Delta = text.Text, ProviderMetadata = providerMetadata },
                        timestamp,
                        textMetadata);
                yield return CreateStreamEvent(
                    eventId,
                    "text-end",
                    new AITextEndEventData { ProviderMetadata = providerMetadata },
                    timestamp,
                    textMetadata);
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return CreateStreamEvent(
            request.Id ?? Guid.NewGuid().ToString("n"),
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = completedAt.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? request.Model ?? string.Empty,
                    completedAt,
                    response.Usage)
            },
            completedAt,
            response.Metadata,
            response.Output);
        await Task.CompletedTask;
    }

    private static Dictionary<string, object>? ToProviderMetadata(Dictionary<string, object?>? metadata)
        => metadata?
            .Where(item => !item.Key.Contains('.', StringComparison.Ordinal) && item.Value is not null)
            .ToDictionary(item => item.Key, item => item.Value!);

    private AIStreamEvent CreateStreamEvent(
        string id,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata,
        AIOutput? output = null)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Id = id,
                Type = type,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata,
                Output = output
            }
        };
}
